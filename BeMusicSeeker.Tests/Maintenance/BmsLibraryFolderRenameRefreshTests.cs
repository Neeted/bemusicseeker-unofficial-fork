using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryFolderRenameRefreshTests
{
    /// <summary>
    /// 管理出力と交差する診断計画を混ぜても非交差の二フォルダを移動・保存し、診断を物理対象に戻して正常処理を拒否する誤りを検出します。
    /// </summary>
    /// <remarks>実フォルダを移動し、SQLiteを読み戻して非交差対象の旧登録除去と新登録を確認します。</remarks>
    [TestMethod]
    public void AutoRenamePreparedPlans_DiagnosticsDoNotBlockDisjointPhysicalMoves()
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string managed = Path.Combine(root, "Managed");
            string first = Path.Combine(root, "First");
            string second = Path.Combine(root, "Second");
            string[] sources = [first, second, managed];
            foreach (string source in sources)
            {
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "chart.bms"), source);
            }
            ChartFile[] charts = sources.Select((source, index) => (ChartTestValues.Empty() with { Path = Path.Combine(source, "chart.bms"), Md5 = index.ToString("x32"), Token = new OwnedChartToken() })).ToArray();
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = managed };
            var library = new TestBmsLibrary(songDbPath, null, null, null, new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => options)
            { BmsCharts = charts };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(charts);
            var planningFailure = new IOException("rename planning failed");
            FolderAutoRenamePlan[] plans =
            [
                new() { SourceDirectory = first, DestinationDirectory = first + "Moved" },
                new() { SourceDirectory = managed, DestinationDirectory = managed + "Failed", FailureException = planningFailure },
                new() { SourceDirectory = managed, DestinationDirectory = managed },
                new() { SourceDirectory = second, DestinationDirectory = second + "Moved" }
            ];
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable accepted));
            using (accepted)
            using (LibraryFileMutationCapability capability = library.OperationAdmission.CreateMutationCapability(accepted))
            {
                Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable held));
                using (held)
                {
                    AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress(charts, false,
                        new RecordingFolderAutoRenameProgressWriter(), reportAtTerminal: true, capability: capability, preparedBatch: new FolderMoveBatch(plans));
                    Assert.AreEqual(2, result.AppliedPlanCount);
                    Assert.AreEqual(2, result.SessionReceipt.ConfirmedChangeCount);
                    Assert.AreSame(planningFailure, result.Diagnostics.Single(item => item.Kind == AutoRenameBatchDiagnosticKind.RenamePlanFailed).Failure);
                    Assert.IsTrue(File.Exists(charts[2].Path));
                    using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                    foreach (string source in new[] { first, second })
                    {
                        Assert.IsFalse(Directory.Exists(source));
                        string moved = Path.Combine(source + "Moved", "chart.bms");
                        Assert.AreEqual(source, File.ReadAllText(moved));
                        Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(source, "chart.bms")));
                        Assert.IsNotNull(db.Find<LR2SongDB.song>(moved));
                    }
                }
            }
            Assert.IsFalse(library.OperationAdmission.IsActive);
        });
    }

    /// <summary>
    /// 物理改名の失敗を未確定結果へ残し、終端報告指定に従いモデル通知を保留または物理処理後に一回だけ表示することを確認します。
    /// </summary>
    /// <param name="reportAtTerminal">trueは上位終端へ報告を委ね、falseは下位入口の報告を確認します。</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RenameChartFolder_FailureDialogRunsAfterFilesystemAndOnlyOnce(bool reportAtTerminal)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderFailureDeferred_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "chart.bms"), "#PLAYER 1");
            List<string> phases = [];
            var fileMutationService = new TestFileMutationService
            {
                MoveDirectoryFailureSourcePath = sourceDirectoryPath,
                OperationObserver = phase => phases.Add(phase)
            };
            var dialogService = new RecordingDialogService(phases);
            try
            {
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    fileMutationService,
                    dialogService);
                int baselineCatalogRevision = library.CatalogRevision;

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "Renamed",
                    reportAtTerminal: reportAtTerminal);

                Assert.IsNotNull(receipt);
                Assert.IsFalse(receipt.DurableCommit);
                Assert.IsNotNull(receipt.PhysicalFailure);
                Assert.AreEqual(sourceDirectoryPath, receipt.FailedTarget.SourcePath);
                Assert.AreEqual(Path.Combine(tempRootPath, "Renamed"), receipt.FailedTarget.DestinationPath);
                Assert.AreEqual(baselineCatalogRevision, library.CatalogRevision);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogService.CallCount);
                Assert.AreEqual(reportAtTerminal ? "filesystem" : "filesystem|dialog", string.Join("|", phases));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 宛先フォルダが既にあれば終端報告指定に関わらず事前通知し、物理処理と両フォルダの変更を行わないことを確認します。
    /// </summary>
    /// <param name="reportAtTerminal">trueは上位終端へ報告を委ね、falseは下位入口の報告を確認します。</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RenameChartFolder_DestinationExistsRetainsPreflightDialog(bool reportAtTerminal)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.Combine(Path.GetDirectoryName(songDbPath)!, "preflight-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var dialogs = new RecordingDialogService();
            var mutations = new List<string>();
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null,
                    new TestFileMutationService { OperationObserver = mutations.Add }, dialogs);
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(source, "destination", reportAtTerminal: reportAtTerminal);
                Assert.IsNull(receipt);
                Assert.AreEqual(1, dialogs.CallCount);
                Assert.AreEqual(0, mutations.Count);
                Assert.IsTrue(Directory.Exists(source));
                Assert.IsTrue(Directory.Exists(destination));
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    /// <summary>
    /// 二種類の拡張子変更を一回の保存・反映・公開へ集約し、実ファイルと保存パスの更新漏れや対象ごとの二重公開を検出します。
    /// </summary>
    /// <remarks>実ファイルの拡張子変更後にSQLiteの旧・新パス件数を読み戻し、確定receiptと公開回数を確認します。</remarks>
    [TestMethod]
    public void RenameBMSFilesExtensionsWithReceipt_MultipleExtensionFamiliesUseSingleSession()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.Combine(
                Path.GetDirectoryName(songDbPath)!,
                "extension-session-" + Guid.NewGuid().ToString("N"));
            string bSource = Path.Combine(root, "alpha.bme");
            string pSource = Path.Combine(root, "beta.pms");
            string bDestination = Path.Combine(root, "alpha.bmx");
            string pDestination = Path.Combine(root, "beta.pmx");
            Directory.CreateDirectory(root);
            File.WriteAllText(bSource, "#PLAYER 1\r\n#TITLE Alpha\r\n#BPM 120\r\n");
            File.WriteAllText(pSource, "#PLAYER 1\r\n#TITLE Beta\r\n#BPM 120\r\n");
            ChartFile bFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bSource));
            bFile = bFile with { Token = new OwnedChartToken() };
            ChartFile pFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pSource));
            pFile = pFile with { Token = new OwnedChartToken() };
            try
            {
                using (var db = new LR2SongDBExtended(songDbPath))
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bFile), typeof(LR2SongDB.song));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(pFile), typeof(LR2SongDB.song));
                }
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService());
                OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [bFile, pFile]);
                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.CatalogRevision))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt(
                    [
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(bFile)],
                            ".bmx"),
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(pFile)],
                            ".pmx")
                    ],
                    unregister: false);

                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(2, receipt.ConfirmedChangeCount);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    Lr2SyncCount = 1,
                    RequiredPublicationCount = 1
                }, receipt.ApplyCounts);
                Assert.AreEqual(2, receipt.CatalogChartPathChangeCount);
                Assert.IsNull(receipt.ApplyFailure);
                Assert.IsNull(receipt.FinalizationFailure);
                Assert.AreEqual(1, ownedCollectionPublicationCount);
                Assert.AreEqual(1, normalRefreshPublicationCount);
                Assert.IsFalse(File.Exists(bSource));
                Assert.IsFalse(File.Exists(pSource));
                Assert.IsTrue(File.Exists(bDestination));
                Assert.IsTrue(File.Exists(pDestination));
                using var readback = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.AreEqual(0, readback.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path IN (?, ?);",
                    bSource,
                    pSource));
                Assert.AreEqual(2, readback.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path IN (?, ?);",
                    bDestination,
                    pDestination));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 二つの拡張子変更が共に物理失敗したとき、入力順の対象別失敗と元実体を保持し、下位入口での重複表示を検出します。
    /// </summary>
    [TestMethod]
    public void RenameBMSFilesExtensionsWithReceipt_FilesystemFailuresStayInSessionForTerminalAggregation()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.Combine(
                Path.GetDirectoryName(songDbPath)!,
                "extension-session-failure-" + Guid.NewGuid().ToString("N"));
            string bSource = Path.Combine(root, "alpha.bme");
            string pSource = Path.Combine(root, "beta.pms");
            Directory.CreateDirectory(root);
            File.WriteAllText(bSource, "#PLAYER 1\r\n#TITLE Alpha\r\n#BPM 120\r\n");
            File.WriteAllText(pSource, "#PLAYER 1\r\n#TITLE Beta\r\n#BPM 120\r\n");
            ChartFile bFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bSource));
            bFile = bFile with { Token = new OwnedChartToken() };
            ChartFile pFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pSource));
            pFile = pFile with { Token = new OwnedChartToken() };
            var mutations = new TestFileMutationService();
            mutations.MoveFileFailureSourcePaths.Add(bSource);
            mutations.MoveFileFailureSourcePaths.Add(pSource);
            var dialogs = new RecordingDialogService();
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, mutations, dialogs);
                OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [bFile, pFile]);

                LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt(
                    [
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(bFile)],
                            ".bmx"),
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(pFile)],
                            ".pmx")
                    ],
                    unregister: false);

                Assert.IsFalse(receipt.DurableCommit);
                Assert.AreEqual(0, receipt.ConfirmedChangeCount);
                Assert.AreEqual(2, receipt.ItemFailures.Count);
                Assert.AreEqual(bSource, receipt.ItemFailures[0].Target.SourcePath);
                Assert.AreEqual(pSource, receipt.ItemFailures[1].Target.SourcePath);
                Assert.AreEqual(0, dialogs.CallCount,
                    "receiptを返す入口は対象別失敗を上位の終端報告へ委ねます。");
                Assert.IsTrue(File.Exists(bSource));
                Assert.IsTrue(File.Exists(pSource));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSフォルダ改名の共通通知でパス・表示フォルダ・関連世代を一回更新し、公開時の未反映状態と通知の重複を検出します。
    /// </summary>
    [TestMethod]
    public async Task RenameChartFolder_UpdatesFolderCellWithoutStorageRowCollectionNotification()
    {

        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                int bmsFilesChangedCount = 0;
                int folderChangedCount = 0;
                int pathChangedCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var folderChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pathChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                        bmsFilesPublished.TrySetResult(true);
                    }
                };
                library.BmsCharts = [file];
                // 初期の共通通知を待ってから、操作の通知を捕捉します。
                await TestUiDispatcherHost.AwaitNotificationAsync(bmsFilesPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
                file = library.BmsCharts.Single(chart => chart.Path == file.Path);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                library.DuplicateChartGroups = [];
                int baselineCatalogRevision = library.CatalogRevision;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
                int catalogRevisionChangedCount = 0;
                int parentFolderVersionChangedCount = 0;
                bool stateAvailableAtOwnedCollectionNotification = false;
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.CatalogRevision))
                    {
                        Interlocked.Increment(ref catalogRevisionChangedCount);
                        ChartFile current = library.BmsCharts.Single(chart => ReferenceEquals(chart.Token, file.Token));
                        stateAvailableAtOwnedCollectionNotification = string.Equals(current.Folder, "Renamed", StringComparison.Ordinal)
                            && current.Path.Contains(Path.Combine("Renamed", "chart.bms"), StringComparison.OrdinalIgnoreCase);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BMSParentFolderListCacheVersion))
                    {
                        Interlocked.Increment(ref parentFolderVersionChangedCount);
                    }
                };
                ChartFile capturedBeforeRename = file;
                int handledVersion = library.NormalLibraryRefreshNotificationVersion;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        return;
                    }
                    NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
                    ChartFile? changed = batch.ChangedCharts.SingleOrDefault(chart => ReferenceEquals(chart.Token, capturedBeforeRename.Token));
                    if (changed is null)
                    {
                        return;
                    }
                    file = changed;
                    if (changed.Folder != capturedBeforeRename.Folder)
                    {
                        Interlocked.Increment(ref folderChangedCount);
                        folderChanged.TrySetResult(true);
                    }
                    if (changed.Path != capturedBeforeRename.Path)
                    {
                        Interlocked.Increment(ref pathChangedCount);
                        pathChanged.TrySetResult(true);
                    }
                };

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");

                await TestUiDispatcherHost.AwaitNotificationAsync(Task.WhenAll(folderChanged.Task, pathChanged.Task), Task.CompletedTask, "folder-rename.common-refresh");
                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref folderChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref pathChangedCount));
                Assert.AreEqual("Renamed", CurrentOwnedChart(library, file).Folder);
                Assert.IsTrue(CurrentOwnedChart(library, file).Path.Contains(Path.Combine("Renamed", "chart.bms")));
                Assert.AreEqual(baselineCatalogRevision + 1, library.CatalogRevision);
                Assert.AreEqual(1, Volatile.Read(ref catalogRevisionChangedCount));
                Assert.IsTrue(stateAvailableAtOwnedCollectionNotification);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, Volatile.Read(ref parentFolderVersionChangedCount));
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 改名の公開時には元フォルダ除去と所持パス更新が済み、新しい変更受付を取得できることを確認して確定前・解放前の公開を検出します。
    /// </summary>
    /// <remarks>実フォルダ改名の確定receiptと、公開時の物理状態・所持パス・変更受付を観測します。</remarks>
    [TestMethod]
    public void RenameChartFolder_PublishesAfterDurableFinalizerAndLeaseRelease()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RenamePublicationBoundary_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Renamed");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService());
                ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                library.BmsCharts = [file];

                int publicationCount = 0;
                bool publicationObservedFinalizedFilesystem = false;
                bool publicationObservedReleasedLease = false;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName != nameof(BMSLibrary.CatalogRevision))
                    {
                        return;
                    }
                    Interlocked.Increment(ref publicationCount);
                    publicationObservedFinalizedFilesystem = !Directory.Exists(sourceDirectoryPath)
                        && Directory.Exists(destinationDirectoryPath)
                        && string.Equals(CurrentOwnedChart(library, file).Path, destinationChartPath, StringComparison.OrdinalIgnoreCase);
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "normal_rename_publication_probe");
                    publicationObservedReleasedLease = probe != null;
                };

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "Renamed");

                Assert.IsNotNull(receipt);
                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(1, Volatile.Read(ref publicationCount));
                Assert.IsTrue(publicationObservedFinalizedFilesystem);
                Assert.IsTrue(publicationObservedReleasedLease);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// LR2の必須反映が失敗しても改名とDB確定を保持し、元失敗・候補パスを残して成功通知を行わないことを確認します。
    /// </summary>
    /// <remarks>SQLiteのLR2反映を拒否するtriggerを使い、改名後の保存行と成功通知の不在を確認します。</remarks>
    [TestMethod]
    public void RenameChartFolder_Lr2FinalizationFailureKeepsDurableStateWithoutSuccessPublication()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_RenameDurableFinalizationFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "PackFinalizationFailure");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE Durable finalization failure\r\n");
            try
            {
                ChartFile file = (ChartTestValues.Empty() with { Path = sourceChartPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, tempRootPath);
                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                var library = new TestBmsLibrary(
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    })
                {
                    BmsCharts = [file]
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.CatalogRevision))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "PackFinalizationFailure");

                Assert.IsNotNull(receipt);
                Assert.IsTrue(receipt.DurableCommit);
                Assert.IsTrue(receipt.HasDurableFinalizationFailure);
                Assert.IsNotNull(receipt.ApplyFailure);
                Assert.AreSame(receipt.ApplyFailure, receipt.PrimaryFailure);
                Assert.IsNull(receipt.CleanupFailure);
                CollectionAssert.Contains(receipt.CandidatePaths.ToArray(), sourceDirectoryPath);
                CollectionAssert.Contains(receipt.CandidatePaths.ToArray(), destinationDirectoryPath);
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(destinationChartPath, CurrentOwnedChart(library, file).Path);
                Assert.AreEqual(0, ownedCollectionPublicationCount);
                Assert.AreEqual(0, normalRefreshPublicationCount);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(sourceChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 背景譜面を含む所持集合で二回改名しても対象範囲だけを訪問し、実体・利用者列・索引を揃え、旧スナップショットの書換えと全件再構築を検出します。
    /// </summary>
    /// <remarks>実フォルダ改名後のSQLite保存パス・利用者列を読み戻し、対象範囲の訪問数と所持索引の更新を確認します。</remarks>
    /// <param name="autoRename">手動改名と書式による自動改名の二入口を通します。</param>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(16, true)]
    public void RenameIngress_CapturesOnlyLocalBmsRangeFacts(int backgroundChartCount, bool autoRename)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_LocalBmsScopeCapture_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstSourceDirectoryPath = Path.Combine(libraryRootPath, "TargetSource1");
            string secondSourceDirectoryPath = Path.Combine(libraryRootPath, "TargetSource2");
            string backgroundDirectoryPath = Path.Combine(libraryRootPath, "Background");
            string firstTargetChartPath = Path.Combine(firstSourceDirectoryPath, "target-1.bms");
            string secondTargetChartPath = Path.Combine(secondSourceDirectoryPath, "target-2.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(backgroundDirectoryPath);
            File.WriteAllText(
                firstTargetChartPath,
                "#PLAYER 1\r\n#TITLE Target Title 1\r\n#ARTIST Target Artist 1\r\n");
            File.WriteAllText(
                secondTargetChartPath,
                "#PLAYER 1\r\n#TITLE Target Title 2\r\n#ARTIST Target Artist 2\r\n");
            try
            {
                ChartFile firstTarget = (ChartTestValues.Empty() with { Path = firstTargetChartPath });
                firstTarget = firstTarget with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstTarget = firstTarget with { Title = "Target Title 1", RawTitle = "Target Title 1" };
                firstTarget = firstTarget with { Artist = "Target Artist 1", RawArtist = "Target Artist 1" };
                firstTarget = firstTarget with { Favorite = 1 };
                ChartFile secondTarget = (ChartTestValues.Empty() with { Path = secondTargetChartPath });
                secondTarget = secondTarget with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondTarget = secondTarget with { Title = "Target Title 2", RawTitle = "Target Title 2" };
                secondTarget = secondTarget with { Artist = "Target Artist 2", RawArtist = "Target Artist 2" };
                secondTarget = secondTarget with { Favorite = 0 };
                var files = new List<ChartFile> { firstTarget, secondTarget };
                for (int index = 0; index < backgroundChartCount; index++)
                {
                    ChartFile background = ChartTestValues.Empty() with
                    {
                        Path = Path.Combine(backgroundDirectoryPath, $"background-{index:D3}.bms")
                    };
                    background = background with { Md5 = index.ToString("x32") };
                    files.Add(background);
                }

                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, libraryRootPath);

                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                var library = new TestBmsLibrary(
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath,
                        FolderNameFormat = "[%ARTIST%] %TITLE%"
                    })
                {
                    BmsCharts = files
                };
                // warm操作前のfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
                {
                    foreach (ChartFile file in files)
                    {
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    }
                });

                BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary =
                    library.WarmInstalledPrimaryHashLookup("配置変更初期primary");
                Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
                OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot initialInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool initialPlaylistCacheHit,
                    out int initialPlaylistStaleRetries);
                Assert.IsFalse(initialPlaylistCacheHit);
                Assert.AreEqual(0, initialPlaylistStaleRetries);
                Assert.IsTrue(initialHash.ContainsMd5(firstTarget.Md5));
                Assert.IsTrue(initialHash.ContainsMd5(secondTarget.Md5));
                Assert.IsTrue(initialInstalled.ContainsPrimaryHash(firstTarget.Md5));
                Assert.IsTrue(initialInstalled.ContainsPrimaryHash(secondTarget.Md5));
                Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, firstTarget.Path));
                Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, secondTarget.Path));

                List<string> hashWork = [];
                List<string> playlistWork = [];
                List<string> installedWork = [];
                library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
                library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
                library.InstalledChartLookupStoreWorkObserver = installedWork.Add;

                (ChartFile Target, string SourceDirectory, string DestinationName, int Favorite)[] operations =
                {
                    (firstTarget, firstSourceDirectoryPath, autoRename
                        ? "[Target Artist 1] Target Title 1"
                        : "Renamed1", 1),
                    (secondTarget, secondSourceDirectoryPath, autoRename
                        ? "[Target Artist 2] Target Title 2"
                        : "Renamed2", 0)
                };
                BMSLibrary.OwnedAdjacentIndexWarmupResult before = library.WarmOwnedRealPathDirectoryView("配置変更warmup前");
                foreach ((ChartFile target, string sourceDirectoryPath, string destinationName, int favorite) in operations)
                {
                    string oldChartPath = target.Path;
                    if (autoRename)
                    {
                        AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress(
                            [(target)], false, new RecordingFolderAutoRenameProgressWriter());
                        Assert.IsTrue(result.HasDurableCommit);
                    }
                    else
                    {
                        LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                            sourceDirectoryPath,
                            destinationName);
                        Assert.IsNotNull(receipt);
                        Assert.IsTrue(receipt.DurableCommit);
                    }

                    BMSLibrary.OwnedAdjacentIndexWarmupResult after = library.WarmOwnedRealPathDirectoryView("配置変更warmup後");
                    int countQueryDelta = after.BmsCountQueryCount - before.BmsCountQueryCount;
                    int rangeQueryDelta = after.BmsRangeQueryCount - before.BmsRangeQueryCount;
                    int visitedReferenceDelta =
                        after.BmsRangeVisitedReferenceCount - before.BmsRangeVisitedReferenceCount;
                    int returnedPathDelta = after.BmsRangeReturnedPathCount - before.BmsRangeReturnedPathCount;
                    Assert.IsTrue(countQueryDelta > 0, "配置変更ごとにBMS祖先件数queryを実行すること。");
                    Assert.IsTrue(rangeQueryDelta > 0, "配置変更ごとにBMS範囲queryを実行すること。");
                    Assert.IsTrue(returnedPathDelta > 0, "対象BMSのexact pathを配置変更receiptへ保持すること。");
                    Assert.IsTrue(
                        visitedReferenceDelta <= 1,
                        $"対象1譜面の局所範囲queryが訪問したref数は1以下であること（実測{visitedReferenceDelta}、背景{backgroundChartCount}）。");
                    before = after;

                    string destinationDirectoryPath = Path.Combine(libraryRootPath, destinationName);
                    string destinationChartPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(oldChartPath));
                    Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                    Assert.IsTrue(File.Exists(destinationChartPath));
                    Assert.AreEqual(destinationChartPath, library.BmsCharts.Single(chart => chart.Md5 == target.Md5).Path);

                    // ここはSELECT専用の観測なので、writer接続を保持せずread-only入口を使う。
                    using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                    {
                        string[] dbPaths = verifySongDb.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                        Assert.IsFalse(dbPaths.Contains(oldChartPath, StringComparer.OrdinalIgnoreCase));
                        Assert.IsTrue(dbPaths.Contains(destinationChartPath, StringComparer.OrdinalIgnoreCase));
                        LR2SongDB.song row = verifySongDb.Table<LR2SongDB.song>().Single(
                            candidate => string.Equals(candidate.path, destinationChartPath, StringComparison.OrdinalIgnoreCase));
                        Assert.AreEqual((int?)favorite, row.favorite);
                        Assert.AreEqual(files.Count, library.BmsCharts.Count);
                        foreach (ChartFile file in library.BmsCharts)
                        {
                            Assert.IsTrue(
                                dbPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase),
                                "配置変更後も対象と未対象のsong rowを保持します。");
                        }
                    }

                    OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                    OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                    InstalledChartLookupIndexSnapshot updatedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                    InstalledChartLookupIndexSnapshot cachedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                    BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                        library.WarmInstalledPrimaryHashLookup("配置変更primary");
                    BMSLibrary.InstalledPrimaryHashWarmupResult cachedPrimary =
                        library.WarmInstalledPrimaryHashLookup("配置変更primary");
                    PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool updatedPlaylistCacheHit,
                        out int updatedPlaylistStaleRetries);
                    PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool cachedPlaylistCacheHit,
                        out int cachedPlaylistStaleRetries);

                    Assert.AreSame(updatedHash, cachedHash);
                    Assert.AreEqual(initialHash.Version, updatedHash.Version);
                    Assert.AreEqual(initialHash.Md5Count, updatedHash.Md5Count);
                    Assert.AreEqual(initialHash.Sha256Count, updatedHash.Sha256Count);
                    Assert.IsTrue(updatedHash.ContainsMd5(firstTarget.Md5));
                    Assert.IsTrue(updatedHash.ContainsMd5(secondTarget.Md5));
                    Assert.AreSame(updatedInstalled, cachedInstalled);
                    Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(target.Md5));
                    Assert.IsFalse(updatedInstalled.GetDistinctDirectoriesByPrimaryHash(target.Md5)
                        .Contains(sourceDirectoryPath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(updatedInstalled.GetDistinctDirectoriesByPrimaryHash(target.Md5)
                        .Contains(destinationDirectoryPath, StringComparer.OrdinalIgnoreCase));
                    Assert.AreEqual("cached", updatedPrimary.Status);
                    Assert.AreEqual("cached", cachedPrimary.Status);
                    Assert.IsTrue(updatedPrimary.FullDirectoryLookupInitialized);
                    Assert.IsTrue(updatedPlaylistCacheHit);
                    Assert.AreEqual(0, updatedPlaylistStaleRetries);
                    Assert.IsTrue(cachedPlaylistCacheHit);
                    Assert.AreEqual(0, cachedPlaylistStaleRetries);
                    Assert.AreSame(updatedPlaylist, cachedPlaylist);
                    Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, oldChartPath));
                    Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, destinationChartPath));
                    Assert.IsTrue(initialInstalled.ContainsPrimaryHash(target.Md5));
                    Assert.IsTrue(initialInstalled.GetDistinctDirectoriesByPrimaryHash(target.Md5)
                        .Contains(sourceDirectoryPath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, oldChartPath));
                    Assert.IsFalse(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, destinationChartPath));
                }

                Assert.IsTrue(
                    hashWork.Count(operation => operation == "owned_hash_source_enumeration") == 0,
                    "配置変更後のowned hash getterがsource全体を再列挙しました。");
                Assert.IsTrue(
                    playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration") == 0,
                    "配置変更後のplaylist resolve getterがsource全体を再列挙しました。");
                Assert.IsTrue(
                    installedWork.Count(operation => operation == "installed_primary_hash_count_update") <= 8,
                    "配置変更後にinstalled lookupを全件再構築しました。");
                Assert.IsTrue(
                    installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                    "実配置変更のinstalled lookup差分更新を観測できませんでした。");
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 二フォルダの自動改名を一回で反映・通知し、資源索引と後続BMS・BMSONの導入先推定が新パスを使うことを確認します。
    /// </summary>
    [TestMethod]
    public void AutoRenameChartFolders_BatchesMultipleFolderMutationsIntoOneRefresh()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameBatch_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE First Title\r\n#ARTIST First Artist\r\n#WAVAA first.wav\r\n#00111:AA\r\n");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE Second Title\r\n#ARTIST Second Artist\r\n");
            File.WriteAllText(Path.Combine(firstDirectoryPath, "first.wav"), "audio");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = ChartTestValues.Empty() with
                {
                    Path = firstChartPath
                };
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "First Title", RawTitle = "First Title" };
                firstFile = firstFile with { Artist = "First Artist", RawArtist = "First Artist" };
                ChartFile secondFile = ChartTestValues.Empty() with
                {
                    Path = secondChartPath
                };
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Second Title", RawTitle = "Second Title" };
                secondFile = secondFile with { Artist = "Second Artist", RawArtist = "Second Artist" };
                library.BmsCharts = [firstFile, secondFile];
                var replacementIndex = new LibraryResourceIndex();
                replacementIndex.DirectoryLookupCache.AddDir(firstDirectoryPath, new[] { "first.wav" });
                replacementIndex.DirectoryLookupCache.AddDir(secondDirectoryPath, new[] { "second.wav" });
                LibraryResourceIndexOwner resourceIndexOwner = GetLibraryResourceIndexOwner(library);
                resourceIndexOwner.Replace(replacementIndex);
                List<(int Total, int Processed, string Path)> progress = [];
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref refreshCount);
                    }
                };
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile)
                ], false, new RecordingFolderAutoRenameProgressWriter(update => progress.Add((update.TotalCount, update.ProcessedCount, update.CurrentPath))));
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref refreshCount));
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    ReverseLookupApplyCount = 1,
                    Lr2SyncCount = 1,
                    RequiredPublicationCount = 1
                }, result.SessionReceipt.ApplyCounts);
                Assert.IsTrue(batch.ChangedCharts.Count > 0);
                Assert.AreEqual(0, batch.DeletedTokens.Count);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsFalse(batch.ResetsPriorNotifications);
                Assert.AreEqual(2, progress.Last().Processed);
                Assert.IsTrue(progress.All(item => item.Total == 2));
                Assert.AreEqual(secondDirectoryPath, progress.Last().Path);
                Assert.AreEqual(Path.Combine(libraryRootPath, "[First Artist] First Title", "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(Path.Combine(libraryRootPath, "[Second Artist] Second Title", "second.bms"), CurrentOwnedChart(library, secondFile).Path);
                LibraryResourceIndexSnapshot resourceSnapshot = resourceIndexOwner.CaptureSnapshot();
                Assert.IsNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(firstDirectoryPath));
                Assert.IsNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(secondDirectoryPath));
                Assert.IsNotNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(Path.Combine(libraryRootPath, "[First Artist] First Title")));
                Assert.IsNotNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(Path.Combine(libraryRootPath, "[Second Artist] Second Title")));
                Assert.IsFalse(Directory.Exists(firstDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDirectoryPath));

                string pendingDirectoryPath = Path.Combine(tempRootPath, "PendingAfterRename");
                string pendingChartPath = Path.Combine(pendingDirectoryPath, "pending.bms");
                Directory.CreateDirectory(pendingDirectoryPath);
                File.WriteAllText(
                    pendingChartPath,
                    "#PLAYER 1\r\n#TITLE First Title\r\n#ARTIST First Artist\r\n#WAVAA first.wav\r\n#00111:AA\r\n");
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage(
                    [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingChartPath))]);
                pendingPackage.path = pendingDirectoryPath;
                pendingPackage.delete_parent = false;
                library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingEntry = pendingPackage.ChartEntries.Single();
                Assert.AreEqual(
                    Path.Combine(libraryRootPath, "[First Artist] First Title"),
                    pendingEntry.Chart.InstallDestination);
                Assert.AreNotEqual(firstDirectoryPath, pendingEntry.Chart.InstallDestination);

                string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "pending.bmson");
                File.WriteAllText(
                    pendingBmsonPath,
                    "{\"version\":\"1.0.0\",\"info\":{\"title\":\"First Title\",\"artist\":\"First Artist\",\"mode_hint\":\"beat-7k\"},"
                    + "\"sound_channels\":[{\"name\":\"first.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}");
                var pendingBmsonPackage = ChartPackage.FromChartEntries(
                [
                    PackageChartEntry.FromChart(
                        (ChartTestValues.ReadBmson(pendingBmsonPath)))
                ]);
                pendingBmsonPackage.path = pendingBmsonPath;
                pendingBmsonPackage.delete_parent = true;
                library.ChartPackagesPending = CreatePackageCollection([pendingPackage, pendingBmsonPackage]);

                library.SearchEstimatedInstallationDirectory(pendingBmsonPackage);

                PackageChartEntry pendingBmsonEntry = pendingBmsonPackage.ChartEntries.Single();
                Assert.AreEqual(
                    Path.Combine(libraryRootPath, "[First Artist] First Title"),
                    pendingBmsonEntry.Chart.InstallDestination);
                Assert.AreNotEqual(firstDirectoryPath, pendingBmsonEntry.Chart.InstallDestination);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 同じ書式で同名になる二フォルダを衝突しない連番の宛先へ移し、二件目の上書きや所持パスの取り違えを検出します。
    /// </summary>
    [TestMethod]
    public void AutoRenameChartFolders_ResolvesBatchDestinationCollisionsWithSuffix()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameCollision_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = ChartTestValues.Empty() with
                {
                    Path = firstChartPath
                };
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "Same Title", RawTitle = "Same Title" };
                firstFile = firstFile with { Artist = "Same Artist", RawArtist = "Same Artist" };
                ChartFile secondFile = ChartTestValues.Empty() with
                {
                    Path = secondChartPath
                };
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Same Title", RawTitle = "Same Title" };
                secondFile = secondFile with { Artist = "Same Artist", RawArtist = "Same Artist" };
                library.BmsCharts = [firstFile, secondFile];

                library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile)
                ], false, new RecordingFolderAutoRenameProgressWriter());

                string firstDestinationPath = Path.Combine(libraryRootPath, "[Same Artist] Same Title");
                string secondDestinationPath = Path.Combine(libraryRootPath, "[Same Artist] Same Title (2)");
                Assert.AreEqual(Path.Combine(firstDestinationPath, "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(Path.Combine(secondDestinationPath, "second.bms"), CurrentOwnedChart(library, secondFile).Path);
                Assert.IsFalse(Directory.Exists(firstDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationPath));
                Assert.IsTrue(Directory.Exists(secondDestinationPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 改名後にLR2の旧パス長を超える譜面名でも物理改名と所持パス更新を完了し、旧制限による処理拒否を検出します。
    /// </summary>
    [TestMethod]
    public void AutoRenameChartFolders_DoesNotFailWhenLongChartFileNameExceedsLr2LegacyPathLimit()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameLongName_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "S");
            string sourceDirectoryPath = Path.Combine(libraryRootPath, "O");
            int fileNameBaseLength = 252 - sourceDirectoryPath.Length - Path.DirectorySeparatorChar.ToString().Length - ".bms".Length;
            if (fileNameBaseLength < 1)
            {
                Assert.Inconclusive("一時パスが長いため、LR2の旧パス長境界を作れません。");
            }
            string chartFileName = new string('x', fileNameBaseLength) + ".bms";
            string chartPath = Path.Combine(sourceDirectoryPath, chartFileName);
            try
            {
                Directory.CreateDirectory(sourceDirectoryPath);
                File.WriteAllText(chartPath, "#PLAYER 1");
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                file = file with { Title = "T", RawTitle = "T" };
                file = file with { Artist = "A", RawArtist = "A" };
                library.BmsCharts = [file];

                library.AutoRenameChartFoldersWithProgress([(file)], false, new RecordingFolderAutoRenameProgressWriter());

                string destinationDirectoryPath = Path.Combine(libraryRootPath, "[A] T");
                Assert.AreEqual(Path.Combine(destinationDirectoryPath, chartFileName), CurrentOwnedChart(library, file).Path);
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 自動改名のLR2必須反映失敗を確定後の不成功として返し、移動・DB・確定件数を保ったまま成功公開を止めることを確認します。
    /// </summary>
    /// <remarks>SQLiteのLR2反映を拒否するtriggerを使い、移動後の保存行・確定receiptと成功通知の不在を確認します。</remarks>
    [TestMethod]
    public void AutoRenameChartFolders_Lr2FinalizationFailureReturnsDurableNonSuccess()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_AutoRenameDurableFinalizationFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "[Artist] PackFinalizationFailure");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE PackFinalizationFailure\r\n#ARTIST Artist\r\n");
            try
            {
                ChartFile file = (ChartTestValues.Empty() with { Path = sourceChartPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                file = file with { Title = "PackFinalizationFailure", RawTitle = "PackFinalizationFailure" };
                file = file with { Artist = "Artist", RawArtist = "Artist" };
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, tempRootPath);
                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                var library = new TestBmsLibrary(
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath,
                        FolderNameFormat = "[%ARTIST%] %TITLE%"
                    })
                {
                    BmsCharts = [file]
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.CatalogRevision))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress(
                    [(file)], false, new RecordingFolderAutoRenameProgressWriter());

                Assert.IsNotNull(result);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    Lr2SyncCount = 1
                }, result.SessionReceipt.ApplyCounts);
                Assert.IsTrue(result.HasDurableFinalizationFailure);
                Assert.IsNotNull(result.PrimaryFailure);
                Assert.AreEqual(1, result.AppliedPlanCount);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogFolderPathChangeCount);
                Assert.AreSame(result.SessionReceipt.ApplyFailure, result.PrimaryFailure.SourceException);
                Assert.IsTrue(result.SessionReceipt.HasDurableFinalizationFailure);
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(destinationChartPath, CurrentOwnedChart(library, file).Path);
                Assert.AreEqual(0, ownedCollectionPublicationCount);
                Assert.AreEqual(0, normalRefreshPublicationCount);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(sourceChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 二件目の物理失敗時には一件目だけを保存・一回公開し、失敗対象と未処理の後続を区別して無断続行や成功分の巻戻しを検出します。
    /// </summary>
    /// <remarks>実フォルダ移動の二件目を失敗させ、SQLiteの保存行を読み戻して成功分と未処理分を区別します。</remarks>
    /// <param name="reportAtTerminal">trueは上位終端へ報告を委ね、falseは下位入口の報告を確認します。</param>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyAutoRenamePlans_UnexpectedMoveFailureCommitsConfirmedPrefixAndStopsSuffix(bool reportAtTerminal)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenamePartialBatch_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            // 祖先を先に扱う長さ順の計画へ、確定対象・失敗対象・未処理対象の順で兄弟フォルダを並べます。
            string firstDirectoryPath = Path.Combine(libraryRootPath, "A");
            string firstDestinationPath = Path.Combine(libraryRootPath, "[First Artist] First Renamed");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "BB");
            string secondDestinationPath = Path.Combine(libraryRootPath, "[Second Artist] Second Renamed");
            string thirdDirectoryPath = Path.Combine(libraryRootPath, "CCC");
            string thirdDestinationPath = Path.Combine(libraryRootPath, "[Third Artist] Third Renamed");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            string thirdChartPath = Path.Combine(thirdDirectoryPath, "third.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            Directory.CreateDirectory(thirdDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            File.WriteAllText(thirdChartPath, "#PLAYER 1");
            try
            {
                var fileMutationService = new TestFileMutationService
                {
                    MoveDirectoryFailureSourcePath = secondDirectoryPath
                };
                var dialogs = new FileDbReportRecordingDialogs();
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogs)
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "First Renamed", RawTitle = "First Renamed" };
                firstFile = firstFile with { Artist = "First Artist", RawArtist = "First Artist" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Second Renamed", RawTitle = "Second Renamed" };
                secondFile = secondFile with { Artist = "Second Artist", RawArtist = "Second Artist" };
                ChartFile thirdFile = (ChartTestValues.Empty() with { Path = thirdChartPath });
                thirdFile = thirdFile with { Token = new OwnedChartToken() };
                thirdFile = thirdFile with { Md5 = "cccccccccccccccccccccccccccccccc" };
                thirdFile = thirdFile with { Title = "Third Renamed", RawTitle = "Third Renamed" };
                thirdFile = thirdFile with { Artist = "Third Artist", RawArtist = "Third Artist" };
                library.BmsCharts = [firstFile, secondFile, thirdFile];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(thirdFile), typeof(LR2SongDB.song));
                }
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref refreshCount);
                    }
                };
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile),
                    (thirdFile)
                ], false, new RecordingFolderAutoRenameProgressWriter(), reportAtTerminal: reportAtTerminal);

                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.ModelMessages);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.AreEqual(1, result.AppliedPlanCount);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogFolderPathChangeCount);
                Assert.IsNotNull(result.SessionReceipt.PhysicalFailure);
                Assert.AreEqual(secondDirectoryPath, result.SessionReceipt.FailedTarget.SourcePath);
                Assert.AreEqual(secondDestinationPath, result.SessionReceipt.FailedTarget.DestinationPath);
                Assert.AreEqual(1, result.SessionReceipt.UnprocessedTargets.Count);
                Assert.AreEqual(thirdDirectoryPath, result.SessionReceipt.UnprocessedTargets[0].SourcePath);
                Assert.AreEqual(thirdDestinationPath, result.SessionReceipt.UnprocessedTargets[0].DestinationPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref refreshCount));
                Assert.IsTrue(batch.ChangedCharts.Count > 0);
                Assert.AreEqual(0, batch.DeletedTokens.Count);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsFalse(batch.ResetsPriorNotifications);
                Assert.AreEqual(Path.Combine(firstDestinationPath, "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(secondChartPath, secondFile.Path);
                Assert.AreEqual(thirdChartPath, thirdFile.Path);
                Assert.IsFalse(Directory.Exists(firstDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationPath));
                Assert.IsTrue(Directory.Exists(secondDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDestinationPath));
                Assert.IsTrue(Directory.Exists(thirdDirectoryPath));
                Assert.IsFalse(Directory.Exists(thirdDestinationPath));
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(firstChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(Path.Combine(firstDestinationPath, "first.bms")));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(secondChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(thirdChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 確定後の公開先が例外を投げても二件の改名成功を保ち、通知中の再受付と後続の共通更新通知を妨げないことを確認します。
    /// </summary>
    /// <remarks>実フォルダ改名の確定receiptを読み、公開中の再受付と購読例外後の共通更新通知を観測します。</remarks>
    [TestMethod]
    public void AutoRenameChartFolders_PublicPublicationFailureIsBestEffortAfterLeaseRelease()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_AutoRenamePublicationFailure_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstSourceDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string secondSourceDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "First Title", RawTitle = "First Title" };
                firstFile = firstFile with { Artist = "First Artist", RawArtist = "First Artist" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Second Title", RawTitle = "Second Title" };
                secondFile = secondFile with { Artist = "Second Artist", RawArtist = "Second Artist" };
                library.BmsCharts = [firstFile, secondFile];
                int publicationCount = 0;
                int postReleasePublicationCount = 0;
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        refreshCount++;
                        return;
                    }
                    if (args.PropertyName != nameof(BMSLibrary.CatalogRevision))
                    {
                        return;
                    }
                    publicationCount++;
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "auto_rename_publication_probe");
                    if (probe != null)
                    {
                        postReleasePublicationCount++;
                    }
                    if (publicationCount == 1)
                    {
                        throw new InvalidOperationException("public publication failure");
                    }
                };

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile)
                ], false, new RecordingFolderAutoRenameProgressWriter());

                Assert.IsNotNull(result);
                Assert.IsTrue(result.HasActionablePlan);
                Assert.AreEqual(2, result.AppliedPlanCount);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsFalse(result.ManualRecoveryRequired);
                Assert.IsNull(result.PrimaryFailure);
                Assert.AreEqual(2, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.CatalogFolderPathChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.FolderReferenceMoveCount);
                Assert.IsNull(result.SessionReceipt.PhysicalFailure);
                Assert.IsNull(result.SessionReceipt.ApplyFailure);
                Assert.IsNull(result.SessionReceipt.FinalizationFailure);
                Assert.AreEqual(1, publicationCount);
                Assert.AreEqual(1, postReleasePublicationCount);
                Assert.AreEqual(1, refreshCount);
                Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(Path.Combine(libraryRootPath, "[First Artist] First Title")));
                Assert.IsTrue(Directory.Exists(Path.Combine(libraryRootPath, "[Second Artist] Second Title")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSのルート移動を共通通知のBMS変更として渡し、BMSONとの種類取り違えや所持パス更新の欠落を検出します。
    /// </summary>
    [TestMethod]
    public async Task MoveLibraryRootFolder_BmsChart_NotifiesBmsStorageRowsThroughRefreshNotification()
    {

        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string chartPath = Path.Combine(sourceRootPath, "chart.bms");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                int bmsFilesChangedCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                        bmsFilesPublished.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                library.BmsCharts = [file];
                await TestUiDispatcherHost.AwaitNotificationAsync(bmsFilesPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile((file))], destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.IsTrue(Volatile.Read(ref normalLibraryRefreshCount) > 0);
                Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(CurrentOwnedChart(library, file).Path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bms")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSONのフォルダ改名を共通通知で一回公開し、表示行のフォルダと所持パスに旧配置を残す誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task RenameBmsonFolder_UpdatesFolderThroughCommonRefresh()
    {

        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = chartPath,
                    Folder = sourceDirectoryPath,
                    Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    RawTitle = "Chart"
                };
                song = song with { Token = new OwnedChartToken() };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                        bmsonSongsPublished.TrySetResult(true);
                    }
                };
                bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.BmsonCharts = [song];
                await TestUiDispatcherHost.AwaitNotificationAsync(bmsonSongsPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");
                var row = LibraryChartRow.FromChartFile(CurrentOwnedChart(library, song));

                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.AreEqual("Renamed", row.Folder);
                Assert.IsTrue(CurrentOwnedChart(library, song).Path.Contains(Path.Combine("Renamed", "chart.bmson")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 二件の物理移動後にDB適用を拒否すると、移動済み事実・元登録・適用失敗を保持して未確定状態を成功公開しないことを確認します。
    /// </summary>
    /// <remarks>SQLite適用を拒否するtriggerを使い、移動済み実ファイルと保持された元保存行を別々に確認します。</remarks>
    /// <param name="reportAtTerminal">trueは上位終端へ報告を委ね、falseは下位入口の報告を確認します。</param>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MoveLibraryRootFolder_DatabaseApplyFailureKeepsConfirmedPhysicalMoves(bool reportAtTerminal)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderSessionDbFailure_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string firstSourceDirectoryPath = Path.Combine(sourceRootPath, "A");
            string secondSourceDirectoryPath = Path.Combine(sourceRootPath, "BB");
            string destinationRootPath = Path.Combine(tempRootPath, "DestinationRoot");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            string firstDestinationChartPath = Path.Combine(destinationRootPath, "A", "first.bms");
            string secondDestinationChartPath = Path.Combine(destinationRootPath, "BB", "second.bms");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(destinationRootPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE First");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE Second");
            try
            {
                var dialogs = new RecordingDialogService();
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    dialogs);
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                library.BmsCharts = [firstFile, secondFile];
                int baselineCatalogRevision = library.CatalogRevision;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
                    string escapedPath = firstDestinationChartPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER fail_folder_session_move BEFORE INSERT ON song WHEN NEW.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced folder session move failure'); END;");
                }

                LibraryMutationSessionReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile((firstFile)),
                        LibraryChartRef.FromChartFile((secondFile))
                    ],
                    destinationRootPath,
                    reportAtTerminal: reportAtTerminal);

                Assert.AreEqual(2, receipt.ConfirmedChangeCount);
                Assert.IsFalse(receipt.DurableCommit);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1
                }, receipt.ApplyCounts);
                Assert.IsNull(receipt.PhysicalFailure);
                Assert.IsNotNull(receipt.ApplyFailure);
                Assert.AreEqual(0, receipt.UnprocessedTargets.Count);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.CallCount);
                Assert.AreEqual(baselineCatalogRevision, library.CatalogRevision);
                Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsTrue(File.Exists(firstDestinationChartPath));
                Assert.IsTrue(File.Exists(secondDestinationChartPath));
                Assert.AreEqual(firstChartPath, CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(secondChartPath, CurrentOwnedChart(library, secondFile).Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(firstChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(secondChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(firstDestinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(secondDestinationChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// ルート移動の二件目が失敗すると最初の一件だけを保存・公開し、失敗対象と未処理対象の実体・登録を保つことを確認します。
    /// </summary>
    /// <remarks>実フォルダ移動の二件目を失敗させ、SQLiteを読み戻して成功分の新登録と残る元登録を確認します。</remarks>
    /// <param name="reportAtTerminal">trueは上位終端へ報告を委ね、falseは下位入口の報告を確認します。</param>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MoveLibraryRootFolder_PhysicalFailureCommitsConfirmedPrefixAndStopsSuffix(bool reportAtTerminal)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderSessionPartial_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string firstSourceDirectoryPath = Path.Combine(sourceRootPath, "A");
            string secondSourceDirectoryPath = Path.Combine(sourceRootPath, "BB");
            string thirdSourceDirectoryPath = Path.Combine(sourceRootPath, "CCC");
            string destinationRootPath = Path.Combine(tempRootPath, "DestinationRoot");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            string thirdChartPath = Path.Combine(thirdSourceDirectoryPath, "third.bms");
            string firstDestinationDirectoryPath = Path.Combine(destinationRootPath, "A");
            string secondDestinationDirectoryPath = Path.Combine(destinationRootPath, "BB");
            string thirdDestinationDirectoryPath = Path.Combine(destinationRootPath, "CCC");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(thirdSourceDirectoryPath);
            Directory.CreateDirectory(destinationRootPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            File.WriteAllText(thirdChartPath, "#PLAYER 1");
            try
            {
                var fileMutationService = new TestFileMutationService
                {
                    MoveDirectoryFailureSourcePath = secondSourceDirectoryPath
                };
                var dialogs = new RecordingDialogService();
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogs);
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                ChartFile thirdFile = (ChartTestValues.Empty() with { Path = thirdChartPath });
                thirdFile = thirdFile with { Token = new OwnedChartToken() };
                thirdFile = thirdFile with { Md5 = "cccccccccccccccccccccccccccccccc" };
                library.BmsCharts = [firstFile, secondFile, thirdFile];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(thirdFile), typeof(LR2SongDB.song));
                }
                int refreshCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        refreshCount++;
                    }
                };

                LibraryMutationSessionReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile((firstFile)),
                        LibraryChartRef.FromChartFile((secondFile)),
                        LibraryChartRef.FromChartFile((thirdFile))
                    ],
                    destinationRootPath,
                    reportAtTerminal: reportAtTerminal);

                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                Assert.AreEqual(1, receipt.CatalogChartPathChangeCount);
                Assert.AreEqual(1, receipt.FolderReferenceMoveCount);
                Assert.IsNotNull(receipt.PhysicalFailure);
                Assert.AreEqual(secondSourceDirectoryPath, receipt.FailedTarget.SourcePath);
                Assert.AreEqual(secondDestinationDirectoryPath, receipt.FailedTarget.DestinationPath);
                Assert.AreEqual(1, receipt.UnprocessedTargets.Count);
                Assert.AreEqual(thirdSourceDirectoryPath, receipt.UnprocessedTargets[0].SourcePath);
                Assert.AreEqual(thirdDestinationDirectoryPath, receipt.UnprocessedTargets[0].DestinationPath);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.CallCount);
                Assert.AreEqual(1, refreshCount);
                Assert.AreEqual(Path.Combine(firstDestinationDirectoryPath, "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(secondChartPath, CurrentOwnedChart(library, secondFile).Path);
                Assert.AreEqual(thirdChartPath, CurrentOwnedChart(library, thirdFile).Path);
                Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationDirectoryPath));
                Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDestinationDirectoryPath));
                Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(thirdDestinationDirectoryPath));
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(firstChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(Path.Combine(firstDestinationDirectoryPath, "first.bms")));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(secondChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(thirdChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSONのルート移動を共通通知のBMSON変更として渡し、BMSとの種類取り違えや所持パス更新の欠落を検出します。
    /// </summary>
    [TestMethod]
    public async Task MoveLibraryRootFolder_BmsonChart_NotifiesBmsonStorageRowsThroughRefreshNotification()
    {

        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonMoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string chartPath = Path.Combine(sourceRootPath, "chart.bmson");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = chartPath,
                    Folder = sourceRootPath,
                    Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    RawTitle = "Chart"
                };
                song = song with { Token = new OwnedChartToken() };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                        bmsonSongsPublished.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.BmsonCharts = [song];
                await TestUiDispatcherHost.AwaitNotificationAsync(bmsonSongsPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile((song))], destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.IsTrue(Volatile.Read(ref normalLibraryRefreshCount) > 0);
                Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(CurrentOwnedChart(library, song).Path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bmson")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSとBMSONの別フォルダ移動を一回の反映と通知へ集約し、形式を跨ぐ更新漏れと対象ごとの二重公開を検出します。
    /// </summary>
    /// <remarks>実フォルダ移動の確定receiptと、二形式の所持パス・一回の通知を観測します。</remarks>
    [TestMethod]
    public async Task MoveLibraryRootFolder_MixedFoldersPublishesOneOperationNotification()
    {

        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MixedMoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string bmsDirectoryPath = Path.Combine(sourceRootPath, "A");
            string bmsonDirectoryPath = Path.Combine(sourceRootPath, "BB");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string bmsPath = Path.Combine(bmsDirectoryPath, "chart.bms");
            string bmsonPath = Path.Combine(bmsonDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(bmsDirectoryPath);
            Directory.CreateDirectory(bmsonDirectoryPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(bmsPath, "#PLAYER 1");
            File.WriteAllText(bmsonPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = (ChartTestValues.Empty() with { Path = bmsPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = bmsonPath,
                    Folder = bmsonDirectoryPath,
                    Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    RawTitle = "Chart"
                };
                song = song with { Token = new OwnedChartToken() };
                int ownedCollectionPublicationCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsFilesPublished.TrySetResult(true);
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsonSongsPublished.TrySetResult(true);
                    }
                    if (args.PropertyName == nameof(BMSLibrary.CatalogRevision))
                    {
                        Interlocked.Increment(ref ownedCollectionPublicationCount);
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                library.BmsCharts = [file];
                await TestUiDispatcherHost.AwaitNotificationAsync(bmsFilesPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
                bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.BmsonCharts = [song];
                await TestUiDispatcherHost.AwaitNotificationAsync(bmsonSongsPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
                Interlocked.Exchange(ref ownedCollectionPublicationCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int baselineCatalogRevision = library.CatalogRevision;
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                LibraryMutationSessionReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile((file)),
                        LibraryChartRef.FromChartFile((song))
                    ],
                    destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                Assert.AreEqual(2, receipt.ConfirmedChangeCount);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    ReverseLookupApplyCount = 1,
                    Lr2SyncCount = 1,
                    RequiredPublicationCount = 1
                }, receipt.ApplyCounts);
                Assert.AreEqual(2, receipt.CatalogChartPathChangeCount);
                Assert.AreEqual(2, receipt.FolderReferenceMoveCount);
                Assert.AreEqual(baselineCatalogRevision + 1, library.CatalogRevision);
                Assert.AreEqual(1, Volatile.Read(ref ownedCollectionPublicationCount));
                Assert.AreEqual(1, Volatile.Read(ref normalLibraryRefreshCount));
                Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
                Assert.AreEqual(Path.Combine(destinationParentPath, "A", "chart.bms"), CurrentOwnedChart(library, file).Path);
                Assert.AreEqual(Path.Combine(destinationParentPath, "BB", "chart.bmson"), CurrentOwnedChart(library, song).Path);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSONの配置修正で実体と所持パス・表示フォルダ・保存行を移動先へ揃え、元登録を残す不完全な修正を検出します。
    /// </summary>
    /// <remarks>実BMSONの移動先と所持パスを確認し、SQLiteを読み戻して旧登録除去と新しい保存フォルダを確認します。</remarks>
    [TestMethod]
    public void FixInstallationDirectoryCharts_BmsonChartUpdatesSongAndPersistedRow()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRepair_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, "{}");
            try
            {
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = sourceChartPath,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Repair Bmson",
                    Md5 = "0123456789abcdef0123456789abcdef",
                    Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                };
                song = song with { Token = new OwnedChartToken() };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(song), typeof(LR2SongDBExtended.bmson_song));
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonCharts = [song];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    (song),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.AreEqual(destinationChartPath, CurrentOwnedChart(library, song).Path);
                Assert.AreEqual(Path.GetFileName(destinationDirectoryPath), CurrentOwnedChart(library, song).Folder);
                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
                {
                    Assert.AreEqual(0, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    LR2SongDBExtended.bmson_song persistedSong = songDb.Table<LR2SongDBExtended.bmson_song>().Single(row => row.path == destinationChartPath);
                    Assert.AreEqual(destinationDirectoryPath, persistedSong.folder);
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 配置修正後のDB適用拒否を保存失敗として返し、移動済み実体と元DB登録を区別して失敗を成功に読み替える誤りを検出します。
    /// </summary>
    /// <remarks>SQLite適用を拒否するtriggerを使い、移動済み実ファイルと保持された元保存行を別々に確認します。</remarks>
    /// <param name="bmson">BMSとBMSONの保存表・所持集合をそれぞれ観測します。</param>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FixInstallationDirectoryCharts_CatalogApplyFailureKeepsPhysicalMoveAndReportsSession(bool bmson)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairDbFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, bmson ? "chart.bmson" : "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceChartPath));
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, bmson ? "{}" : "#PLAYER 1\r\n#TITLE Repair DB failure\r\n");
            try
            {
                ChartFile? bmsFile = null;
                ChartFile? bmsonSong = null;
                ChartFile chart;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    if (bmson)
                    {
                        bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                        {
                            Path = sourceChartPath,
                            Folder = sourceDirectoryPath,
                            RawTitle = "Repair DB failure",
                            Md5 = "abcdefabcdefabcdefabcdefabcdefab",
                            Sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
                        };
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong), typeof(LR2SongDBExtended.bmson_song));
                        chart = (bmsonSong);
                    }
                    else
                    {
                        bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile), typeof(LR2SongDB.song));
                        chart = (bmsFile);
                    }

                    string escapedDestinationPath = destinationChartPath.Replace("'", "''");
                    string tableName = bmson ? "bmson_song" : "song";
                    songDb.Execute(
                        "CREATE TRIGGER repair_path_insert_failure BEFORE INSERT ON " + tableName
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-path-write-fault'); END;");
                    songDb.Execute(
                        "CREATE TRIGGER repair_path_update_failure BEFORE UPDATE ON " + tableName
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-path-write-fault'); END;");
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsCharts = bmsFile == null ? [] : [bmsFile],
                    BmsonCharts = bmsonSong == null ? [] : [bmsonSong]
                };
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    chart,
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.Failure);
                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsFalse(result.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogChartPathChangeCount);
                Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(sourceChartPath, bmsonSong?.Path ?? bmsFile!.Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                if (bmson)
                {
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == destinationChartPath));
                }
                else
                {
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == destinationChartPath));
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 二件目の配置修正保存が失敗すると二件を同時にロールバックし、物理移動は保持して対象ごとの部分DB確定を検出します。
    /// </summary>
    /// <remarks>SQLiteの二件目の保存を拒否するtriggerを使い、実ファイル移動後も二件の元保存行が残ることを読み戻します。</remarks>
    [TestMethod]
    public void FixInstallationDirectoryCharts_MultipleRepairsShareOneCatalogTransaction()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairBatchDbFailure_" + Guid.NewGuid().ToString("N"));
            string firstSourceDirectory = Path.Combine(tempRootPath, "FirstBroken");
            string secondSourceDirectory = Path.Combine(tempRootPath, "SecondBroken");
            string destinationDirectory = Path.Combine(tempRootPath, "Installed");
            string firstSourcePath = Path.Combine(firstSourceDirectory, "first.bmson");
            string secondSourcePath = Path.Combine(secondSourceDirectory, "second.bmson");
            string firstDestinationPath = Path.Combine(destinationDirectory, "first.bmson");
            string secondDestinationPath = Path.Combine(destinationDirectory, "second.bmson");
            Directory.CreateDirectory(firstSourceDirectory);
            Directory.CreateDirectory(secondSourceDirectory);
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllText(firstSourcePath, "{}");
            File.WriteAllText(secondSourcePath, "{}");
            try
            {
                ChartFile firstSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = firstSourcePath,
                    Folder = firstSourceDirectory,
                    RawTitle = "First repair",
                    Md5 = "11111111111111111111111111111111",
                    Sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                };
                firstSong = firstSong with { Token = new OwnedChartToken() };
                ChartFile secondSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = secondSourcePath,
                    Folder = secondSourceDirectory,
                    RawTitle = "Second repair",
                    Md5 = "22222222222222222222222222222222",
                    Sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
                };
                secondSong = secondSong with { Token = new OwnedChartToken() };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(firstSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(secondSong), typeof(LR2SongDBExtended.bmson_song));
                    string escapedDestinationPath = secondDestinationPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER repair_batch_insert_failure BEFORE INSERT ON bmson_song"
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-batch-write-fault'); END;");
                    songDb.Execute(
                        "CREATE TRIGGER repair_batch_update_failure BEFORE UPDATE ON bmson_song"
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-batch-write-fault'); END;");
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsonCharts = [firstSong, secondSong]
                };
                ChartFile firstTarget = ChartFileProjection.WithPackageState(
                    (firstSong), destinationDirectory, string.Empty, string.Empty, []);
                ChartFile secondTarget = ChartFileProjection.WithPackageState(
                    (secondSong), destinationDirectory, string.Empty, string.Empty, []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts(
                    [firstTarget, secondTarget],
                    approvedDuplicateRemovalChartPaths: []);

                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsNotNull(result.Failure);
                Assert.IsFalse(result.SessionReceipt.DurableCommit);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1
                }, result.SessionReceipt.ApplyCounts);
                Assert.AreEqual(2, result.MovedCount);
                Assert.AreEqual(2, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.PackageInstallDestinationChangeCount);
                Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
                Assert.IsFalse(File.Exists(firstSourcePath));
                Assert.IsFalse(File.Exists(secondSourcePath));
                Assert.IsTrue(File.Exists(firstDestinationPath));
                Assert.IsTrue(File.Exists(secondDestinationPath));
                Assert.AreEqual(firstSourcePath, CurrentOwnedChart(library, firstSong).Path);
                Assert.AreEqual(secondSourcePath, CurrentOwnedChart(library, secondSong).Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == firstSourcePath));
                Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == secondSourcePath));
                Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == firstDestinationPath));
                Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == secondDestinationPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 異なる内容の同名ファイルを保持して別名へ修正し、実際に選んだ宛先を所持集合とDBへ反映することを確認します。
    /// </summary>
    /// <remarks>実ファイルの衝突内容を確認し、選択された別名パスが所持集合とSQLite保存行に反映されることを観測します。</remarks>
    /// <param name="bmson">BMSとBMSONの保存表・所持集合をそれぞれ観測します。</param>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FixInstallationDirectoryCharts_UsesCollisionPathForOwnerAndDb(bool bmson)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairCollision_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, bmson ? "chart.bmson" : "chart.bms");
            string collisionPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceChartPath));
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, bmson ? "{}" : "#PLAYER 1\r\n#TITLE Repair collision\r\n");
            File.WriteAllText(collisionPath, bmson ? "{\"existing\":true}" : "#PLAYER 1\r\n#TITLE Existing collision\r\n");
            byte[] collisionBytes = File.ReadAllBytes(collisionPath);
            try
            {
                ChartFile? bmsFile = null;
                ChartFile? bmsonSong = null;
                ChartFile chart;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    if (bmson)
                    {
                        bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                        {
                            Path = sourceChartPath,
                            Folder = sourceDirectoryPath,
                            RawTitle = "Repair collision",
                            Md5 = "abcdefabcdefabcdefabcdefabcdefab",
                            Sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
                        };
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong), typeof(LR2SongDBExtended.bmson_song));
                        chart = (bmsonSong);
                    }
                    else
                    {
                        bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile), typeof(LR2SongDB.song));
                        chart = (bmsFile);
                    }
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsCharts = bmsFile == null ? [] : [bmsFile],
                    BmsonCharts = bmsonSong == null ? [] : [bmsonSong]
                };
                chart = bmson ? library.BmsonCharts.Single() : library.BmsCharts.Single();
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    chart,
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.IsNotNull(result);
                Assert.IsNull(result.Failure);
                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsTrue(result.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, result.SessionReceipt.CatalogChartPathChangeCount);
                string actualDestinationPath = result.SessionReceipt.ConfirmedTargets.Single().DestinationPath;
                Assert.AreNotEqual(collisionPath, actualDestinationPath, ignoreCase: true);
                Assert.IsTrue(File.Exists(actualDestinationPath));
                Assert.IsTrue(File.Exists(sourceChartPath) == false);
                Assert.IsTrue(collisionBytes.SequenceEqual(File.ReadAllBytes(collisionPath)));
                Assert.AreEqual(actualDestinationPath, CurrentOwnedChart(library, chart).Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                if (bmson)
                {
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == actualDestinationPath));
                }
                else
                {
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == actualDestinationPath));
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 配置修正と同じ予約で移動先の資源を再評価・保存し、予約解放後に公開します。保守保存の拒否でも移動確定と元失敗を保持します。
    /// </summary>
    /// <remarks>実資源を移動先で再評価し、SQLite保守行の読戻しと通知時の受付・健全性状態を確認します。</remarks>
    /// <param name="bmson">BMSとBMSONの保存表・所持集合をそれぞれ観測します。</param>
    /// <param name="failMaintenance">移動後の保守保存だけを拒否する条件です。</param>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void FixInstallationDirectoryCharts_RechecksResourcesUnderExistingReservation(bool bmson, bool failMaintenance)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Broken");
            string destination = Path.Combine(root, "Installed");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string sourcePath = Path.Combine(source, bmson ? "chart.bmson" : "chart.bms");
            string destinationPath = Path.Combine(destination, Path.GetFileName(sourcePath));
            File.WriteAllText(sourcePath, bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Repair\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
                    + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}"
                : "#PLAYER 1\r\n#TITLE Repair\r\n#WAV01 sound.wav\r\n#00111:01\r\n");
            File.WriteAllText(Path.Combine(destination, "sound.wav"), "present only at the correct destination");
            ChartFile? bmsFile = bmson ? null : BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath));
            ChartFile? bmsonSong = bmson ? ChartTestValues.ReadBmson(sourcePath) : null;
            ChartFile chart = (bmson ? (bmsonSong!) : (bmsFile!)) with { Token = new OwnedChartToken() };
            ResourceHealthMaintenanceSnapshot initialInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
            Assert.AreEqual(1, initialInfo.WavFilesDefined);
            Assert.AreEqual(0, initialInfo.WavFilesExisting);
            chart = ChartFileProjection.WithMaintenance(chart, initialInfo);
            if (bmsonSong is not null)
            {
                bmsonSong = chart;
            }
            else if (bmsFile is not null)
            {
                bmsFile = chart;
            }

            chart = bmson ? (bmsonSong!) : (bmsFile!);
            Assert.IsTrue(new BmsLibraryMaintenanceService().BuildResourceHealthWarnings(chart)
                .Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(db);
                if (bmson)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong!), typeof(LR2SongDBExtended.bmson_song));
                }
                else
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile!), typeof(LR2SongDB.song));
                }

                db.InsertOrReplace(MaintenanceStorageMapping.ToStorage(initialInfo), typeof(LR2SongDBExtended.maintenance));
                if (failMaintenance)
                {
                    // パス移転時の旧健全性0は通し、その後の資源再検査保存だけを拒否します。
                    db.Execute("CREATE TRIGGER repair_health_insert BEFORE INSERT ON maintenance WHEN NEW.wav_files_existing = 1 BEGIN SELECT RAISE(ABORT, 'repair-health-write-fault'); END;");
                    db.Execute("CREATE TRIGGER repair_health_update BEFORE UPDATE ON maintenance WHEN NEW.wav_files_existing = 1 BEGIN SELECT RAISE(ABORT, 'repair-health-write-fault'); END;");
                }
            }
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = bmsFile == null ? [] : [bmsFile],
                BmsonCharts = bmsonSong == null ? [] : [bmsonSong]
            };
            ChartFile repairTarget = ChartFileProjection.WithPackageState(chart, destination, string.Empty, string.Empty, []);
            int refreshCount = 0;
            bool notifiedWithLeaseHeld = false;
            bool notifiedWithCurrentHealth = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }

                refreshCount++;
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("repair_health_notification_probe");
                notifiedWithLeaseHeld |= probe == null;
                notifiedWithCurrentHealth |= CurrentOwnedChart(library, chart).ResourceHealthMaintenanceSnapshot?.WavFilesExisting == 1;
            };

            LibraryFixInstallationResult? repairResult = null;
            Exception? failure = null;
            try
            {
                repairResult = library.FixInstallationDirectoryCharts([repairTarget]);
                failure = repairResult?.Failure;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(repairResult);
            Assert.IsNotNull(repairResult.SessionReceipt);
            Assert.IsTrue(repairResult.SessionReceipt.DurableCommit);
            Assert.AreEqual(1, repairResult.SessionReceipt.CatalogChartPathChangeCount);
            Assert.AreEqual(destinationPath, CurrentOwnedChart(library, chart).Path);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(refreshCount > 0);
            Assert.IsFalse(notifiedWithLeaseHeld);
            using LibraryFileMutationLease afterRepair = library.TryBeginLibraryFileMutation("repair_health_completion_probe");
            Assert.IsNotNull(afterRepair);
            if (failMaintenance)
            {
                Assert.IsNotNull(failure);
                Assert.IsNotNull(repairResult.SessionReceipt.FinalizationFailure);
                Assert.AreSame(repairResult.SessionReceipt.FinalizationFailure, failure);
                Exception completedFailure = failure!;
                StringAssert.Contains(completedFailure.ToString(), "repair-health-write-fault");
            }
            else
            {
                Assert.IsNull(failure);
                Assert.IsNull(repairResult.SessionReceipt.FinalizationFailure);
                Assert.IsTrue(notifiedWithCurrentHealth);
                ResourceHealthMaintenanceSnapshot updatedInfo = CurrentOwnedChart(library, chart).ResourceHealthMaintenanceSnapshot;
                Assert.AreEqual(1, updatedInfo.WavFilesDefined);
                Assert.AreEqual(1, updatedInfo.WavFilesExisting);
                Assert.AreEqual(destinationPath, updatedInfo.Path);
                ChartFile installed = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();
                Assert.AreEqual(string.Empty, installed.InstallDestination);
                Assert.IsFalse(new BmsLibraryMaintenanceService().BuildResourceHealthWarnings(installed)
                    .Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            }
            using var readback = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            LR2SongDBExtended.maintenance persisted = readback.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == destinationPath);
            Assert.AreEqual(failMaintenance ? 0 : 1, persisted.wav_files_existing);
            Assert.AreEqual(0, readback.Table<LR2SongDBExtended.maintenance>().Count(row => row.path == sourcePath));
        });
    }

    /// <summary>
    /// 配置修正で元DBの登録日時・お気に入り・タグと未変更列を継承し、解析値で保存値を上書きする誤りを検出します。
    /// </summary>
    /// <remarks>実BMSを配置修正した後にSQLiteを読み戻し、元の利用者列と未変更列の継承を確認します。</remarks>
    [TestMethod]
    public void FixInstallationDirectoryCharts_BmsChartPreservesExistingLr2SongUserColumns()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsRepair_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE repaired\r\n");
            try
            {
                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
                file = file with { Token = new OwnedChartToken() };
                string hash = file.Md5;
                ChartFile existing = ChartTestValues.Empty() with
                {
                    Path = sourceChartPath,
                    AddDate = 12345,
                    Tag = "external-user-tag"
                };
                existing = existing with { Md5 = hash };
                existing = existing with { Favorite = 7 };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existing), typeof(LR2SongDB.song));
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsCharts = [file];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    (file),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                using var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", sourceChartPath));
                LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == destinationChartPath);
                Assert.AreEqual(hash, row.hash);
                // 純移転では、利用者列だけでなく旧DB行の未変更の生成列も継承します。
                Assert.AreEqual(string.Empty, row.title);
                Assert.AreEqual("repaired", CurrentOwnedChart(library, file).RawTitle);
                Assert.AreEqual(12345, row.adddate);
                Assert.AreEqual(7, row.favorite);
                Assert.AreEqual("external-user-tag", row.tag);
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.folder));
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.parent));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 物理削除後のDB削除拒否では所持集合・健全性索引・公開世代・元登録を保持し、未保存の削除をconsumerへ公開する誤りを検出します。
    /// </summary>
    /// <remarks>SQLite削除を拒否するtriggerを使い、実ファイル削除後も元登録と所持・索引・公開世代を保持することを確認します。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_DurableCatalogFailureLeavesConsumerStateUnchanged()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogFailure_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string chartPath = Path.Combine(tempRootPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                library.BmsCharts = [file];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    string escapedPath = chartPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER fail_library_delta_remove BEFORE DELETE ON song WHEN OLD.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced library mutation failure'); END;");
                }

                int notificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int catalogRevision = library.CatalogRevision;
                ResourceHealthIndexSnapshot resourceHealthSnapshot = library.GetResourceHealthIndexSnapshotForView("failure_baseline");
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                    sendToRecycleBin: false,
                    approvedWholeFolderDeletePaths: []);

                Assert.AreEqual(notificationVersion, library.NormalLibraryRefreshNotificationVersion);
                Assert.AreEqual(catalogRevision, library.CatalogRevision);
                Assert.IsTrue(outcome.HasError);
                Assert.IsTrue(outcome.CatalogApplyAttempted);
                Assert.IsFalse(outcome.CatalogDurable);
                Assert.IsNotNull(outcome.CatalogFailure);
                Assert.IsFalse(File.Exists(chartPath));
                ResourceHealthIndexSnapshot resourceHealthAfterFailure = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                Assert.AreSame(resourceHealthSnapshot, resourceHealthAfterFailure);
                Assert.AreEqual(resourceHealthSnapshot.Version, resourceHealthAfterFailure.Version);
                Assert.AreEqual(resourceHealthSnapshot.TargetCount, resourceHealthAfterFailure.TargetCount);
                Assert.AreEqual(resourceHealthSnapshot.NeedFixCount, resourceHealthAfterFailure.NeedFixCount);
                Assert.AreEqual(1, library.BmsCharts.Count);
                Assert.AreSame(file, library.BmsCharts.Single());
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 譜面削除の共通通知を実受信した時点で保存行が既に消えていることを確認し、DB確定より先の削除公開を検出します。
    /// </summary>
    [TestMethod]
    public void RemoveLibraryCharts_CommitsCatalogBeforePublishingOwnedCollectionChange()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Committed");
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            Directory.CreateDirectory(chartDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1");
            var library = new TestBmsLibrary(songDbPath);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            library.BmsCharts = [file];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }

            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            bool notificationReached = false;
            bool rowStillExistsWhenNotificationWasPublished = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }
                notificationReached = true;
                using var notificationSongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                rowStillExistsWhenNotificationWasPublished = notificationSongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath);
            };
            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(new LibraryMutationSessionApplyCounts
            {
                CatalogApplyCount = 1,
                PackageReferenceApplyCount = 1,
                ReverseLookupApplyCount = 1,
                Lr2SyncCount = 1,
                RequiredPublicationCount = 1
            }, outcome.SessionReceipt.ApplyCounts);
            Assert.AreEqual(0, library.BmsCharts.Count);
            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue((notificationBatch.ChangedCharts.Count > 0 || notificationBatch.DeletedTokens.Count > 0 || notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
            Assert.IsTrue(notificationReached);
            Assert.IsFalse(rowStillExistsWhenNotificationWasPublished);
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath));
        });
    }

    /// <summary>
    /// 削除公開先が例外を投げても実DBの削除確定と空の所持集合を保持し、通知失敗で確定成功を取り消す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void RemoveLibraryCharts_PublicNotificationFailureKeepsCatalogCommit()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "CommittedNotificationFailure");
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            Directory.CreateDirectory(chartDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1");
            var library = new TestBmsLibrary(songDbPath);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
            library.BmsCharts = [file];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }

            bool notificationAttempted = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.CatalogRevision))
                {
                    notificationAttempted = true;
                    throw new InvalidOperationException("public notification failure");
                }
            };
            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsTrue(notificationAttempted);
            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(0, library.BmsCharts.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath));
        });
    }

    /// <summary>
    /// 構築済み導入索引を改名先へ更新して初期化状態を保ち、旧ディレクトリの残存と索引の無用な失効を検出します。
    /// </summary>
    [TestMethod]
    public void RenameChartFolder_BuiltInstalledLookupMovesPathIncrementally()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LookupMove_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            Directory.CreateDirectory(oldDirectoryPath);
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            try
            {
                string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = oldChartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = hash };
                library.BmsCharts = [file];
                InstalledChartLookupIndexSnapshot initial = InvokeCreateInstalledChartLookupSnapshot(library);
                Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
                CollectionAssert.AreEqual(new[] { oldDirectoryPath }, initial.Md5Directories[hash].ToArray());

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    oldDirectoryPath,
                    "New",
                    unregister: false,
                    renameRootFolder: false);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                InstalledChartLookupIndexSnapshot updated = InvokeCreateInstalledChartLookupSnapshot(library);

                Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
                Assert.AreEqual(newChartPath, CurrentOwnedChart(library, file).Path);
                CollectionAssert.AreEqual(new[] { newDirectoryPath }, updated.Md5Directories[hash].ToArray());
                Assert.IsFalse(updated.KnownChartDirectories.Contains(oldDirectoryPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 所持BMSを別集合へ置換すると構築済み導入索引を失効し、再構築で旧ハッシュ・旧導入先を除くことを確認します。
    /// </summary>
    [TestMethod]
    public void BMSFilesReplacement_InvalidatesBuiltInstalledLookup()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string firstDirectoryPath = Path.Combine("C:\\Installed", "First");
            string secondDirectoryPath = Path.Combine("C:\\Installed", "Second");
            string firstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string secondHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile firstFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(firstDirectoryPath, "chart.bms")
            };
            firstFile = firstFile with { Token = new OwnedChartToken() };
            firstFile = firstFile with { Md5 = firstHash };
            ChartFile secondFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(secondDirectoryPath, "chart.bms")
            };
            secondFile = secondFile with { Token = new OwnedChartToken() };
            secondFile = secondFile with { Md5 = secondHash };

            library.BmsCharts = [firstFile];
            InstalledChartLookupIndexSnapshot initial = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initial.ContainsPrimaryHash(firstHash));

            library.BmsCharts = [secondFile];

            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            InstalledChartLookupIndexSnapshot rebuilt = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(rebuilt.ContainsPrimaryHash(firstHash));
            Assert.IsTrue(rebuilt.ContainsPrimaryHash(secondHash));
            CollectionAssert.AreEqual(new[] { secondDirectoryPath }, rebuilt.Md5Directories[secondHash].ToArray());
        });
    }

    /// <summary>
    /// 同じMD5を持つ二修正では先行の実成功を次の重複判断へ反映し、一件だけを移動・保存して二重移動を検出します。
    /// </summary>
    [TestMethod]
    public void FixInstallationDirectoryCharts_UsesSuccessfulRepairHashOverlayForLaterDuplicate()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairHashOverlay_" + Guid.NewGuid().ToString("N"));
            string firstSourceDirectory = Path.Combine(tempRootPath, "First");
            string secondSourceDirectory = Path.Combine(tempRootPath, "Second");
            string destinationDirectory = Path.Combine(tempRootPath, "Installed");
            Directory.CreateDirectory(firstSourceDirectory);
            Directory.CreateDirectory(secondSourceDirectory);
            Directory.CreateDirectory(destinationDirectory);
            string firstSourcePath = Path.Combine(firstSourceDirectory, "chart.bms");
            string secondSourcePath = Path.Combine(secondSourceDirectory, "chart.bms");
            string destinationPath = Path.Combine(destinationDirectory, "chart.bms");
            const string chartText = "#PLAYER 1\r\n#TITLE Session overlay\r\n#00111:0100\r\n";
            File.WriteAllText(firstSourcePath, chartText);
            File.WriteAllText(secondSourcePath, chartText);
            try
            {
                ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstSourcePath));
                first = first with { Token = new OwnedChartToken() };
                ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondSourcePath));
                second = second with { Token = new OwnedChartToken() };
                Assert.AreEqual(first.Md5, second.Md5);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(first), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(second), typeof(LR2SongDB.song));
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsCharts = [first, second]
                };
                ChartFile firstTarget = ChartFileProjection.WithPackageState(
                    (first), destinationDirectory, string.Empty, string.Empty, []);
                ChartFile secondTarget = ChartFileProjection.WithPackageState(
                    (second), destinationDirectory, string.Empty, string.Empty, []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts(
                    [firstTarget, secondTarget],
                    approvedDuplicateRemovalChartPaths: []);

                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsTrue(result.SessionReceipt.DurableCommit, result.SessionReceipt.PrimaryFailure?.ToString());
                Assert.AreEqual(1, result.MovedCount);
                Assert.AreEqual(1, result.DuplicateSkippedCount);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.PackageInstallDestinationChangeCount);
                Assert.IsFalse(File.Exists(firstSourcePath));
                Assert.IsTrue(File.Exists(destinationPath));
                Assert.IsTrue(File.Exists(secondSourcePath));
                Assert.AreEqual(destinationPath, CurrentOwnedChart(library, first).Path);
                Assert.AreEqual(secondSourcePath, CurrentOwnedChart(library, second).Path);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// BMSON配置修正の重複削除は承認対象だけに限定し、未選択の同梱譜面と既存宛先を保持します。DB拒否時は独立移動も同時に未確定へ残します。
    /// </summary>
    /// <param name="catalogFailure">重複除去の保存を拒否して一括反映の巻戻しを確認します。</param>
    /// <param name="approveDuplicateRemoval">重複実体の削除を明示承認する条件です。</param>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void FixInstallationDirectoryCharts_BmsonDuplicateHonorsApprovalAndKeepsSibling(
        bool catalogFailure,
        bool approveDuplicateRemoval)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRepairDup_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string installedChartPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, "{}");
            File.WriteAllText(installedChartPath, "{}");
            try
            {
                string md5 = "abcdefabcdefabcdefabcdefabcdefab";
                string sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
                ChartFile sourceSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = sourceChartPath,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Repair Duplicate Bmson",
                    Md5 = md5,
                    Sha256 = sha256
                };
                sourceSong = sourceSong with { Token = new OwnedChartToken() };
                ChartFile installedSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = installedChartPath,
                    Folder = destinationDirectoryPath,
                    RawTitle = "Installed Duplicate Bmson",
                    Md5 = md5,
                    Sha256 = sha256
                };
                installedSong = installedSong with { Token = new OwnedChartToken() };
                string movedSource = Path.Combine(sourceDirectoryPath, "other.bmson");
                string movedDestination = Path.Combine(destinationDirectoryPath, "other.bmson");
                File.WriteAllText(movedSource, "{}");
                string siblingSourcePath = Path.Combine(sourceDirectoryPath, "sibling.bmson");
                File.WriteAllText(siblingSourcePath, "{}");
                ChartFile movedSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = movedSource,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Independent repair target",
                    Md5 = "11111111111111111111111111111111",
                    Sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                };
                movedSong = movedSong with { Token = new OwnedChartToken() };
                ChartFile siblingSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = siblingSourcePath,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Unselected sibling",
                    Md5 = "22222222222222222222222222222222",
                    Sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
                };
                siblingSong = siblingSong with { Token = new OwnedChartToken() };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(sourceSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(installedSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(movedSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(siblingSong), typeof(LR2SongDBExtended.bmson_song));
                    if (catalogFailure)
                    {
                        songDb.Execute("CREATE TRIGGER fail_repair_delete BEFORE DELETE ON bmson_song WHEN OLD.path = '"
                            + sourceChartPath.Replace("'", "''") + "' BEGIN SELECT RAISE(ABORT, 'repair-deletion-fault'); END;");
                    }
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonCharts = [sourceSong, installedSong, movedSong, siblingSong];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    (sourceSong),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                ChartFile independentTarget = ChartFileProjection.WithPackageState(
                    (movedSong), destinationDirectoryPath, string.Empty, string.Empty, []);
                IReadOnlyList<string> approvedPaths = approveDuplicateRemoval ? [sourceChartPath] : [];
                LibraryFixInstallationResult repairResult = library.FixInstallationDirectoryCharts(
                    [independentTarget, repairTarget],
                    approvedPaths);
                Assert.IsNotNull(repairResult.SessionReceipt);
                Assert.AreEqual(1, repairResult.DuplicateSkippedCount);
                Assert.AreEqual(1, repairResult.SessionReceipt.CatalogChartPathChangeCount);
                if (approveDuplicateRemoval)
                {
                    Assert.AreEqual(1, repairResult.ApprovedRemovedCount);
                    Assert.AreEqual(1, repairResult.SessionReceipt.CatalogChartRemovalCount);
                    Assert.AreEqual(2, repairResult.SessionReceipt.ConfirmedChangeCount);
                    if (catalogFailure)
                    {
                        Assert.IsNotNull(repairResult.Failure);
                        Assert.IsFalse(repairResult.SessionReceipt.DurableCommit);
                        Assert.IsNotNull(repairResult.SessionReceipt.ApplyFailure);
                    }
                    else
                    {
                        Assert.IsNull(repairResult.Failure);
                        Assert.IsTrue(repairResult.SessionReceipt.DurableCommit);
                    }
                }
                else
                {
                    Assert.IsNull(repairResult.Failure);
                    Assert.AreEqual(0, repairResult.ApprovedRemovedCount);
                    Assert.AreEqual(0, repairResult.SessionReceipt.CatalogChartRemovalCount);
                    Assert.AreEqual(1, repairResult.SessionReceipt.ConfirmedChangeCount);
                }
                Assert.IsFalse(File.Exists(movedSource));
                Assert.IsTrue(File.Exists(movedDestination));

                Assert.AreEqual(approveDuplicateRemoval, !File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(installedChartPath));
                Assert.IsTrue(File.Exists(siblingSourcePath));
                Assert.AreEqual(!approveDuplicateRemoval || catalogFailure, library.BmsonCharts.Any(song => string.Equals(song.Path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonCharts.Any(song => string.Equals(song.Path, installedChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonCharts.Any(song => string.Equals(song.Path, siblingSourcePath, StringComparison.OrdinalIgnoreCase)));
                using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
                {
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 1 : approveDuplicateRemoval ? 0 : 1,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 0 : 1,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == movedDestination));
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 1 : 0,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == movedSource));
                    Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == installedChartPath));
                    Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == siblingSourcePath));
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 文字コード指定を保存して既存表示行へ通知し、警告集合の不要な通知や短命識別の取り替えを検出します。
    /// </summary>
    [TestMethod]
    public void SetBMSFilesEncoding_UpdatesEncodingCellWithoutLibraryCollectionChanged()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_EncodingRefresh_" + Guid.NewGuid().ToString("N"));
            string chartPath = Path.Combine(tempRootPath, "chart.bms");
            Directory.CreateDirectory(tempRootPath);
            File.WriteAllText(
                chartPath,
                "#TITLE Garbled\r\n#ARTIST Artist\r\n#GENRE TEST\r\n",
                Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                file = file with { Token = new OwnedChartToken() };
                file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, encoding = "unknown", is_encoding_fixed = false }));
                int garbledChangedCount = 0;
                int garbledFixedChangedCount = 0;
                int encodingChangedCount = 0;
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.ChartFilesGarbled))
                    {
                        Interlocked.Increment(ref garbledChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.ChartFilesGarbledFixed))
                    {
                        Interlocked.Increment(ref garbledFixedChangedCount);
                    }
                };
                var rowCache = new NormalLibraryRowCache();
                LibraryChartRow? row = null;
                int handledVersion = library.NormalLibraryRefreshNotificationVersion;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion) && row is not null)
                    {
                        NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
                        rowCache.ApplyChanges(batch.ChangedCharts, batch.DeletedTokens, null);
                        handledVersion = batch.LatestVersion;
                    }
                };
                library.BmsCharts = [file];
                file = library.BmsCharts.Single();
                row = rowCache.GetOrCreate(file, null);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(LibraryChartRow.encoding) || string.IsNullOrEmpty(args.PropertyName))
                    {
                        Interlocked.Increment(ref encodingChangedCount);
                    }
                };
                handledVersion = library.NormalLibraryRefreshNotificationVersion;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = chartPath, hash = file.Md5, encoding = "unknown", is_encoding_fixed = false }, typeof(LR2SongDBExtended.maintenance));
                }
                Interlocked.Exchange(ref garbledChangedCount, 0);
                Interlocked.Exchange(ref garbledFixedChangedCount, 0);
                Interlocked.Exchange(ref encodingChangedCount, 0);

                library.SetBMSFilesEncoding([file], "gb2312");

                Assert.AreEqual(0, Volatile.Read(ref garbledChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref garbledFixedChangedCount));
                Assert.IsTrue(Volatile.Read(ref encodingChangedCount) > 0);
                Assert.AreEqual("unknown", file.ResourceHealthMaintenanceSnapshot.Encoding);
                Assert.AreEqual("gb2312", row.encoding);
                Assert.AreSame(file.Token, row.Chart.Token);
                using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                LR2SongDBExtended.maintenance persistedMaintenance = verifySongDb.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == chartPath);
                Assert.AreEqual("gb2312", persistedMaintenance.encoding);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 難易度表の名前・記号変更を明示確定までは捕捉値として保ち、独立参照rangeを配送してcatalog版・commitは増やさないことを確認します。
    /// </summary>
    [TestMethod]
    public async Task ReferenceHeaderCommit_PublishesIndependentRangeWithoutCatalogCommit()
    {

        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var table = new BMSTable
            {
                name = "Before",
                symbol = "A",
                entries = [new BMSTableEntry(file)]
            };
            int bmsFilesChangedCount = 0;
            library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    Interlocked.Increment(ref bmsFilesChangedCount);
                    bmsFilesPublished.TrySetResult(true);
                }
            };
            library.BmsCharts = [file];
            await TestUiDispatcherHost.AwaitNotificationAsync(bmsFilesPublished.Task, Task.CompletedTask, "folder-prepare.common-refresh");
            Interlocked.Exchange(ref bmsFilesChangedCount, 0);
            library.SynchronizeReferenceBMSTables([table]);
            ChartFile chart = (file);
            int handledReferenceVersion = library.NormalLibraryRefreshNotificationVersion;
            int catalogRevision = library.CatalogRevision;
            Interlocked.Exchange(ref bmsFilesChangedCount, 0);

            table.symbol = "B";
            table.name = "After";
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Before", library.GetPlaylistReferenceDisplay(chart).Names);

            library.ApplyPlaylistReferenceEntryCommits([], [new(table, table.symbol, table.name, table.bmt_sort, 0)]);

            Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
            Assert.AreEqual(catalogRevision, library.CatalogRevision);
            NormalLibraryRefreshNotificationBatch referenceBatch = library.GetNormalLibraryRefreshNotificationsAfter(handledReferenceVersion);
            Assert.AreEqual(0, referenceBatch.CatalogCommits.Count);
            Assert.AreEqual(1, referenceBatch.ReferenceCommits.Count);
            Assert.AreEqual("B", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("After", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    /// <summary>
    /// 同じ譜面を参照する旧表を再読込表へ置換し、表示へ旧名前・記号を併記する追記処理を検出します。
    /// </summary>
    [TestMethod]
    public void SynchronizeReferenceBMSTables_ReplacesReloadedPlaylistReferenceWithoutAppending()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            BMSTable oldTable = CreateTable("Before", "A", file.Md5);
            BMSTable newTable = CreateTable("After", "B", file.Md5);
            library.BmsCharts = [file];

            library.SynchronizeReferenceBMSTables([oldTable]);
            ChartFile chart = (file);
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Before", library.GetPlaylistReferenceDisplay(chart).Names);

            library.SynchronizeReferenceBMSTables([newTable]);

            Assert.AreEqual("B", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("After", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    /// <summary>
    /// 準備後に参照世代が変わると古い計画を拒否して現在表示を保ち、新しい計画だけを確定することを確認します。
    /// </summary>
    [TestMethod]
    public void PreparedReferenceSynchronizationCommitsAtomicallyAndRejectsStaleRevision()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            BMSTable currentTable = CreateTable("Before", "A", file.Md5);
            BMSTable replacementTable = CreateTable("After", "B", file.Md5);
            library.BmsCharts = [file];
            library.SynchronizeReferenceBMSTables([currentTable]);
            ChartFile chart = (file);

            BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan stalePlan = library.PrepareReferenceBMSTableSynchronization([replacementTable]);
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);

            currentTable.symbol = "C";
            currentTable.name = "Concurrent";
            library.ApplyPlaylistReferenceEntryCommits([], [new(currentTable, currentTable.symbol, currentTable.name, currentTable.bmt_sort, 0)]);

            Assert.IsFalse(library.TryCommitReferenceBMSTableSynchronization(stalePlan));
            Assert.AreEqual("C", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Concurrent", library.GetPlaylistReferenceDisplay(chart).Names);

            BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan currentPlan = library.PrepareReferenceBMSTableSynchronization([replacementTable]);
            Assert.IsTrue(library.TryCommitReferenceBMSTableSynchronization(currentPlan));
            Assert.AreEqual("B", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("After", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    /// <summary>
    /// 一致・不一致の保留BMSONを混在させても一致MD5だけへ難易度表の名前と記号を反映し、無関係な譜面への参照付与を検出します。
    /// </summary>
    [TestMethod]
    public void PlaylistReferenceRead_ResolvesMatchedPendingBmsonWithoutMaterializing()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            PackageChartEntry matchingBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\matching.bmson",
                matchingHash);
            PackageChartEntry unmatchedBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\unmatched.bmson",
                "cccccccccccccccccccccccccccccccc");
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([matchingBmsonEntry, unmatchedBmsonEntry])
            ]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.SynchronizeReferenceBMSTables([table]);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Names);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(unmatchedBmsonEntry.Chart).Symbols);
        });
    }

    /// <summary>
    /// 所持BMSONへ一致MD5の難易度表を反映し、BMSの保存行を持たない形式を参照更新から除外する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void PlaylistReferenceRead_ResolvesInstalledBmsonWithoutBmsOwner()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"C:\Library\chart.bmson",
                Md5 = matchingHash,
                Sha256 = new string('b', 64),
                RawTitle = "Installed Bmson",
                RawArtist = "Artist"
            };
            song = song with { Token = new OwnedChartToken() };
            library.BmsonCharts = [song];
            ChartFile chart = (song);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.SynchronizeReferenceBMSTables([table]);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    /// <summary>
    /// 明示指定したBMSへ一致ハッシュの表名・記号を反映し、全所持集合だけを対象にする更新漏れを検出します。
    /// </summary>
    [TestMethod]
    public void PlaylistReferenceRead_ResolvesBmsStorageOwner()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            BMSTable table = CreateTable("Matched", "M", file.Md5);

            library.SynchronizeReferenceBMSTables([table]);

            ChartFile chart = (file);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    /// <summary>
    /// 明示指定したBMSONへ一致ハッシュの表名・記号を反映し、BMS以外を対象から落とす更新漏れを検出します。
    /// </summary>
    [TestMethod]
    public void PlaylistReferenceRead_ResolvesBmsonWithoutBmsOwner()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"C:\Library\chart.bmson",
                Md5 = matchingHash,
                Sha256 = new string('b', 64),
                RawTitle = "Library Bmson",
                RawArtist = "Artist"
            };
            ChartFile chart = (song);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.SynchronizeReferenceBMSTables([table]);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    /// <summary>
    /// 所持外の明示パッケージでも一致BMSONだけに表名・記号を反映し、不一致への誤付与と全体所持だけを更新する漏れを検出します。
    /// </summary>
    [TestMethod]
    public void PlaylistReferenceRead_ResolvesInstalledPackageBmsonWithoutMaterializing()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            PackageChartEntry matchingBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Installed\Package\matching.bmson",
                matchingHash);
            PackageChartEntry unmatchedBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Installed\Package\unmatched.bmson",
                "cccccccccccccccccccccccccccccccc");
            var installedPackage = ChartPackage.FromChartEntries([matchingBmsonEntry, unmatchedBmsonEntry]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);
            library.SynchronizeReferenceBMSTables([table]);

            Assert.IsNull(installedPackage.ChartEntries[0].GetBmsChartForTest());
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Names);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(unmatchedBmsonEntry.Chart).Symbols);
        });
    }

    /// <summary>
    /// 旧表だけに一致する保留BMSONは表置換後に参照表示を失い、新表へ一致していない譜面に参照を引き継ぐ誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ReferenceTableReplacement_DoesNotTransferOldOnlyPendingBmsonReference()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            PackageChartEntry oldOnlyBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\old.bmson",
                oldHash);
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([oldOnlyBmsonEntry])
            ]);
            BMSTable oldTable = CreateTable("Old", "O", oldHash);
            BMSTable newTable = CreateTable("New", "N", newHash);
            library.SynchronizeReferenceBMSTables([oldTable]);
            Assert.AreEqual("O", library.GetPlaylistReferenceDisplay(oldOnlyBmsonEntry.Chart).Symbols);

            library.SynchronizeReferenceBMSTables([newTable]);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(oldOnlyBmsonEntry.Chart).Symbols);
        });
    }

    /// <summary>
    /// SHA256で一致した保留BMSONの表行を実削除して参照解除へ渡し、MD5だけを調べて古い表示を残す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ReferenceEntryRemoval_ClearsPendingReferenceMatchedBySha256()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string sha256 = new string('d', 64);
            PackageChartEntry matchingBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\sha.bmson",
                md5: string.Empty,
                sha256: sha256);
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([matchingBmsonEntry])
            ]);
            BMSTable table = CreateTableWithHashes("Sha", "S", md5: string.Empty, sha256: sha256);
            library.SynchronizeReferenceBMSTables([table]);
            Assert.AreEqual("S", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);

            List<BMSTableEntry> removedEntries = [.. table.entries];
            PlaylistEntryChange[] changes = [.. removedEntries.Select(entry => new PlaylistEntryChange(entry, entry.CaptureValue(), null, 0, null))];
            table.RemoveBMSTableEntries(removedEntries);
            library.ApplyPlaylistReferenceEntryCommits([new(table, 0, 1, [.. changes])]);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
        });
    }

    /// <summary>
    /// 内容が未読込になった表でも登録済みの参照を解除し、行一覧を取得できないことを理由に表示を残す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ReferenceTableRemoval_AllowsUnloadedTables()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            file = file with { Md5 = md5 };
            library.BmsCharts = [file];
            BMSTable table = CreateTable("Reference", "R", md5);

            library.SynchronizeReferenceBMSTables([table]);
            ChartFile chart = (file);
            Assert.AreEqual("R", library.GetPlaylistReferenceDisplay(chart).Symbols);

            table.MarkEntriesNotLoaded();
            library.RemovePlaylistReferenceTables([table]);

            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(chart).Symbols);
        });
    }

    private static InstalledChartLookupIndexSnapshot InvokeCreateInstalledChartLookupSnapshot(BMSLibrary library)
    {
        return library.CreateInstalledChartLookupSnapshotForDiagnostics();
    }

    private static LibraryResourceIndexOwner GetLibraryResourceIndexOwner(BMSLibrary library)
    {
        FieldInfo field = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (LibraryResourceIndexOwner)field.GetValue(library)!;
    }

    private static bool IsInstalledChartLookupIndexInitialized(BMSLibrary library)
    {
        return library.IsInstalledChartLookupIndexInitializedForDiagnostics();
    }

    private static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(BMSLibrary library)
    {
        return library.CreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlayForDiagnostics();
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private static PackageChartEntry CreateAdapterlessBmsonEntry(string path, string md5, string sha256 = "")
    {
        return PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = path,
            Md5 = md5,
            Sha256 = string.IsNullOrWhiteSpace(sha256) ? new string('b', 64) : sha256,
            RawTitle = "Pending Bmson",
            RawArtist = "Artist"
        }));
    }

    /// <summary>捕捉後に移転した所持項目の共通現在値を、同じ短命識別で検査します。</summary>
    private static ChartFile CurrentOwnedChart(BMSLibrary library, ChartFile captured)
        => library.BmsCharts.Concat(library.BmsonCharts).Single(chart => ReferenceEquals(chart.Token, captured.Token));

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderRenameRefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            // fixture schemaは初期化処理の検証対象ではないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            });
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

    private static async Task WithTemporarySongDbAsync(Func<string, Task> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SongDbTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            // asyncテスト用fixture schemaも本番処理の検証対象ではないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            });
            await testAction(songDbPath).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }


    private static BMSTable CreateTable(string name, string symbol, string hash)
    {
        return CreateTableWithHashes(name, symbol, hash, sha256: string.Empty);
    }

    private static BMSTable CreateTableWithHashes(string name, string symbol, string md5, string sha256)
    {
        var bMSTable = new BMSTable
        {
            name = name,
            symbol = symbol
        };
        ChartFile testableBmsFile = ChartTestValues.Empty();
        testableBmsFile = testableBmsFile with { Md5 = md5 };
        testableBmsFile = testableBmsFile with { Sha256 = sha256 };
        testableBmsFile = testableBmsFile with { Path = @"C:\Library\chart.bms" };
        var entry = new BMSTableEntry(testableBmsFile)
        {
            is_removed = false
        };
        if (string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
        {
            entry.MarkAsBmsonPlaylistIdentity(sha256);
        }
        bMSTable.entries = [entry];
        return bMSTable;
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        private readonly List<string>? phases;

        internal RecordingDialogService(List<string>? phases = null)
        {
            this.phases = phases;
        }

        internal int CallCount { get; private set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            CallCount++;
            phases?.Add("dialog");
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public string? MoveDirectoryFailureSourcePath { get; set; }

        public HashSet<string> MoveFileFailureSourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action<string>? OperationObserver { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (MoveFileFailureSourcePaths.Contains(sourcePath))
            {
                throw new IOException("Synthetic file move failure for session terminal aggregation test.");
            }
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (!string.IsNullOrWhiteSpace(MoveDirectoryFailureSourcePath)
                && string.Equals(sourcePath, MoveDirectoryFailureSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory move failure for batch behavior test.");
            }
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string? destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (!string.IsNullOrWhiteSpace(MoveDirectoryFailureSourcePath)
                && string.Equals(sourcePath, MoveDirectoryFailureSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory move failure for batch behavior test.");
            }
            CopyDirectoryTree(sourcePath, destinationPath, overwrite);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }

        private static void CopyDirectoryTree(string sourcePath, string destinationPath, bool overwrite)
        {
            CopyDirectory(sourcePath, destinationPath);
            if (!overwrite)
            {
                return;
            }
        }
    }
}

internal sealed class RecordingFolderAutoRenameProgressWriter(
    Action<FolderAutoRenameProgressUpdate>? onUpdate = null) : IFolderAutoRenameProgressWriter
{
    public void TryWrite(FolderAutoRenameProgressUpdate update) => onUpdate?.Invoke(update);
}
