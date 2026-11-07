using System.Diagnostics;
using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧の実folder rename、共有受付、失敗通知と停止時の収束を検証します。</summary>
/// <remarks>実FS・SQLite・LR2終端を用い、物理成功後の終端失敗と表示通知の分担を確認します。compiled編集入口はMainWindowの混在選択caseが分担します。</remarks>
[TestClass]
public sealed class RegularChartFolderRenameTests
{

    /// <summary>終端抑止解除をgateで保持した実renameを停止が待ち、成功と永続終端失敗の両方で物理変更・通知回数・元失敗を回収して途中終了を検出します。</summary>
    [TestMethod]
    // falseは正常終端、trueはfolder INSERT triggerによる永続終端失敗です。
    [DataRow(false)]
    [DataRow(true)]
    public void StopAsync_DrainsInFlightFolderRename(bool finalizationFails)
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "rename-source");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string destinationFolder = finalizationFails
                ? "PackFinalizationFailure"
                : "rename-destination";
            string destinationDirectory = Path.Combine(libraryRoot, destinationFolder);
            string destinationChartPath = Path.Combine(destinationDirectory, "chart.bms");
            string lr2RootPath = Path.Combine(libraryRoot, "LR2beta3");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE shutdown\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var dialogs = new FileDbReportRecordingDialogs();
            TestBmsLibrary library;
            if (finalizationFails)
            {
                LR2Config lr2Config = TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, libraryRoot);
                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                library = new TestBmsLibrary(databaseScope,
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    null,
                    dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    });
                using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }
            }
            else
            {
                library = new TestBmsLibrary(databaseScope,
                    songDbPath,
                    null,
                    null,
                    null,
                    dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            }
            library.BmsCharts = [file];
            ChartFile chart = (file);
            var target = new ChartOperationTarget(
                chart,
                playlistEntry: null,
                ChartOperationSourceScope.Library,
                isOwned: true,
                isPending: false,
                isPlaylistMissing: false,
                ChartOperationCapabilities.MoveInLibrary);
            Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));

            int appliedCount = 0;
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                chartFileOperations: library.OperationAdmission,
                mutationDialogs: dialogs);
            owner.AttachNormalLibraryRefreshSource(library);
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.HasRefreshNotification)
                {
                    Interlocked.Increment(ref appliedCount);
                }
            };
            using var suppressionEntered = new ManualResetEventSlim();
            using var releaseSuppression = new ManualResetEventSlim();
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (!args.IsSuppressed)
                {
                    suppressionEntered.Set();
                    releaseSuppression.Wait();
                }
            };

            Task renameTask = owner.RenameChartFolderAsync(request, destinationFolder);
            Task? stopTask = null;
            try
            {
                suppressionEntered.Wait();

                using var stopStarted = new ManualResetEventSlim();
                stopTask = ConcurrencyProbe.StartDedicatedAsync(async delegate
                {
                    stopStarted.Set();
                    await owner.StopAsync();
                });
                stopStarted.Wait();
                Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));
                Assert.IsFalse(renameTask.IsCompleted, "The rename must still be in flight while shutdown begins draining it.");

                releaseSuppression.Set();
                stopTask.GetAwaiter().GetResult();
                if (finalizationFails)
                {
                    Exception? renameFailure = null;
                    try
                    {
                        renameTask.GetAwaiter().GetResult();
                    }
                    catch (Exception exception)
                    {
                        renameFailure = exception;
                    }
                    Assert.IsNotNull(renameFailure, "The in-flight durable finalization failure must remain observable on the rename task.");
                    StringAssert.Contains(renameFailure!.ToString(), "forced durable finalization failure");
                }
                else
                {
                    renameTask.GetAwaiter().GetResult();
                }
                Assert.IsFalse(Directory.Exists(sourceDirectory));
                Assert.IsTrue(Directory.Exists(destinationDirectory));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(1, Volatile.Read(ref appliedCount), "LR2終端の結果にかかわらずDB確定値を必要配送します。");
                Assert.AreEqual(finalizationFails ? 1 : 0, dialogs.Messages.Count);
            }
            finally
            {
                releaseSuppression.Set();
                try
                {
                    renameTask.GetAwaiter().GetResult();
                }
                catch when (finalizationFails)
                {
                    // 本文が元の永続終端失敗を検査済みで、ここでは開始Taskを回収します。
                }
                finally
                {
                    stopTask?.GetAwaiter().GetResult();
                }
            }
        });
    }

    /// <summary>実LR2終端SQL失敗でも物理・譜面DBの確定値を現在行へ反映し、元SQL失敗と一回通知を保持して必要表示・cleanup後にLを解放します。</summary>
    [TestMethod]
    // falseは案内成功、trueは案内経路もIOExceptionを投げる条件です。
    [DataRow(false)]
    [DataRow(true)]
    public void FolderRename_Lr2FinalizationFailureStillDisplaysCommittedValues(bool reporterThrows)
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "workflow-source");
            string destinationDirectory = Path.Combine(libraryRoot, "PackFinalizationFailure");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectory, "chart.bms");
            string lr2RootPath = Path.Combine(libraryRoot, "LR2beta3");
            var dialogs = new FileDbReportRecordingDialogs();
            if (reporterThrows)
            {
                dialogs.MessageFailure = new IOException("terminal report failed");
            }

            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            bool reportAfterRelease = false;
            bool logicalAdmissionHeldDuringNotification = false;
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE workflow finalization failure\r\n");
            try
            {
                ChartFile file = CreateTestableBmsFile(chartPath);
                LR2Config lr2Config = TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, libraryRoot);
                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                var library = new TestBmsLibrary(databaseScope,
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    null,
                    dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    })
                {
                    BmsCharts = [file]
                };
                gate = library.OperationAdmission;
                dialogs.OnMessage = () =>
                {
                    bool gateReleased = gate.TryEnter(out IDisposable releasedGate);
                    reportAfterRelease = !activity.IsActive && !gateReleased;
                    if (gateReleased)
                    {
                        releasedGate.Dispose();
                    }

                    logicalAdmissionHeldDuringNotification = library.OperationAdmission.IsActive;
                };
                using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                var table = new MainChartListViewModel();
                int normalRefreshApplyCount = 0;
                using RegularChartListOwner owner = CreateOwner(
                    table,
                    CreateWorkspaceForOwner(),
                    action => action(),
                    chartFileOperations: gate,
                    mutationDialogs: dialogs,
                    chartMutationActivity: activity);
                owner.AttachNormalLibraryRefreshSource(library);
                owner.ApplyMainLibraryView(new ChartListRefreshRoute(ChartListRefreshRouteKind.ContinueMainLibrary,
                    MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected,
                    isPlaylistTreeActive: false, includeBmsonRows: false), library, null, null, false, Stopwatch.StartNew());
                LibraryChartRow row = table.Rows[0] as LibraryChartRow ?? throw new AssertFailedException("改名対象の現在行がありません。");
                owner.NormalLibraryRefreshApplied += (_, args) =>
                {
                    if (args.NotificationBatch.HasRefreshNotification)
                    {
                        Interlocked.Increment(ref normalRefreshApplyCount);
                    }
                };
                RenameChartFolderRequest request = CreateRenameRequest(file);

                Task renameTask = owner.RenameChartFolderAsync(request, "PackFinalizationFailure");
                Exception? renameFailure = null;
                try
                {
                    renameTask.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    renameFailure = exception;
                }
                Assert.IsNotNull(renameFailure, "The durable finalization failure must remain observable on the rename task.");

                Assert.IsFalse(Directory.Exists(sourceDirectory));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(chartPath, file.Path);
                Assert.AreEqual(destinationChartPath, library.BmsCharts[0].Path);
                Assert.AreEqual(1, Volatile.Read(ref normalRefreshApplyCount));
                Assert.AreEqual(destinationChartPath, row.path);
                Assert.AreEqual("PackFinalizationFailure", row.Folder);
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages, "Canonical reporting suppresses the lower receipt-backed dialog.");
                Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages[0].Icon);
                Assert.IsTrue(reportAfterRelease);
                Assert.IsTrue(logicalAdmissionHeldDuringNotification);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "forced durable finalization failure");
                StringAssert.Contains(renameFailure!.ToString(), "forced durable finalization failure");
                using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                {
                    Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                    Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(chartPath));
                }
                owner.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                if (Directory.Exists(Path.Combine(libraryRoot, "LR2beta3")))
                {
                    Directory.Delete(Path.Combine(libraryRoot, "LR2beta3"), recursive: true);
                }
                if (Directory.Exists(sourceDirectory))
                {
                    Directory.Delete(sourceDirectory, recursive: true);
                }
                if (Directory.Exists(destinationDirectory))
                {
                    Directory.Delete(destinationDirectory, recursive: true);
                }
            }
        });
    }

}
