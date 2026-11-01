using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PackageCatalogWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageCatalogWorkflowOwnerTests
{
    /// <summary>管理入力の単独・選択・全解除は停止の実終端を待ち、同じLで競合要求を拒否します。停止失敗・終了取消では入力bytesと主失敗を保ち、次の明示要求だけを受け付けます。</summary>
    /// <remarks>実owner→本番store→実SQLite/FSを動かし、停止portだけを制御します。管理外・導入済み・無関係な管理入力への削除拡大も検出します。</remarks>
    [TestMethod]
    // 同一test processの独立compositionが共通session markerを初期公開するfixture都合で非並列とします。本番の同一Lによる直列化と、下記のL競合判定は維持します。
    [DoNotParallelize]
    [DataRow("package", "success")]
    [DataRow("selection", "success")]
    [DataRow("all", "success")]
    [DataRow("package", "failure")]
    [DataRow("all", "cancel")]
    public async Task PendingRemoval_WaitsForStopAndRetainsInputOnFailure(string route, string outcome)
    {
        var databaseScope = new ApplicationDatabaseScope();
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace1.RootDirectory, "bms-catalog-removal-" + Guid.NewGuid().ToString("N"));
        string managed = TempDirectoryPublisher.Get("catalog-removal");
        string unrelated = TempDirectoryPublisher.Get("catalog-unrelated");
        Directory.CreateDirectory(root);
        string chartPath = Path.Combine(managed, "chart.bms");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("#TITLE pending\n");
        File.WriteAllBytes(chartPath, bytes);
        string borrowed = Path.Combine(root, "borrowed.bms");
        string installed = Path.Combine(root, "installed.bms");
        File.WriteAllBytes(borrowed, bytes);
        File.WriteAllBytes(installed, bytes);
        File.WriteAllBytes(Path.Combine(unrelated, "keep.bms"), bytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new ControlledPlayback(() => { entered.TrySetResult(); return stop.Task; });
        Task<PackageCatalogMutationResult>? removal = null;
        TestBmsLibrary? library = null;
        try
        {
            string db = Path.Combine(root, "song.db");
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), db);
            gateway.EnsureAppOwnedSchema();
            gateway.EnsureLibraryStartupSchema();
            library = new TestBmsLibrary(databaseScope, db, null, null, new ResilientFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null));
            var pending = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(chartPath)]);
            pending.path = managed;
            var borrowedPackage = new ChartPackage { path = borrowed };
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([pending, borrowedPackage]);
            library.ChartPackagesInstalled = new ObservableCollection<ChartPackage>([new ChartPackage { path = installed }]);
            gateway.ApplyInstallTableMutation([], [pending, borrowedPackage]);
            var activity = new ChartMutationActivityOwner();
            var owner = new PackageCatalogWorkflowOwner(() => library, library.OperationAdmission, activity,
                AcceptedDialogs(), playback, mutation => Task.Run(mutation));
            bool suppressed = false;
            owner.MutationPhasePublished += (_, e) => suppressed = e.Phase == PackageCatalogMutationPhase.RefreshSuppressionStarted;
            var target = new ChartOperationTarget(pending.ChartEntries[0].Chart, null,
                ChartOperationSourceScope.PendingPackage, false, true, false, ChartOperationCapabilities.UpdateInstallDestination,
                packageEntry: pending.ChartEntries[0]);
            removal = route switch
            {
                "package" => owner.RemovePackageAsync(PackageCatalogSection.Pending, pending),
                "selection" => owner.RemoveSelectionAsync(PackageCatalogRemovalRequest.CreatePending([target])),
                _ => owner.ClearAllAsync(PackageCatalogSection.Pending)
            };
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, removal, "catalog-managed-stop");
            Assert.IsFalse(removal.IsCompleted);
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsTrue(activity.IsActive);
            Assert.IsTrue(suppressed);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(chartPath));
            Assert.IsFalse((await owner.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded, "競合要求を予約しません。");
            Exception? expected = outcome switch
            {
                "failure" => new IOException("stop failed"),
                "cancel" => new OperationCanceledException("shutdown"),
                _ => null
            };
            if (expected == null) { stop.SetResult(); }
            else { stop.SetException(expected); }
            PackageCatalogMutationResult result = await removal;
            Assert.AreEqual(expected == null, result.Succeeded);
            Assert.AreSame(expected, result.Failure);
            if (expected != null) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(chartPath)); }
            else
            {
                Assert.IsFalse(Directory.Exists(managed));
                Assert.AreEqual(route == "all" ? 0 : 1, library.ChartPackagesPending.Count);
                using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(route == "all" ? 0 : 1, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM install;"));
            }
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(borrowed));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(installed));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(unrelated, "keep.bms")));
            Assert.IsFalse(library.OperationAdmission.IsActive);
            Assert.IsFalse(activity.IsActive);
            Assert.IsFalse(suppressed);
            playback.Stop = () => Task.CompletedTask;
            Assert.IsTrue((await owner.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsFalse(Directory.Exists(managed));
        }
        finally
        {
            stop.TrySetResult();
            if (removal != null) { await removal; }
            library?.RequestShutdown("catalog-removal-test");
            TempDirectoryPublisher.TryDeleteManagedPath(managed);
            TempDirectoryPublisher.TryDeleteManagedPath(unrelated);
            Directory.Delete(root, recursive: true);
        }
    }

}
