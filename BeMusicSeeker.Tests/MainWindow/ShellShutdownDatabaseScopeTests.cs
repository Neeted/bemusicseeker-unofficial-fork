using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>実compositionの終了準備が、同じDB所有者の接続寿命と累積close失敗だけを参照することを検査します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class ShellShutdownDatabaseScopeTests
{
    /// <summary>同processの別compositionが実readonly接続を保持していても、接続のないappのCloseは完了します。</summary>
    [TestMethod]
    public async Task WindowClose_LeavesAnotherApplicationsReadOnlyConnectionOpen()
    {
        string root = CreateTemporaryDirectory();
        MainWindowViewModel first = MainWindowViewModelTestFactory.Create();
        MainWindowViewModel second = MainWindowViewModelTestFactory.Create();
        ApplicationDatabaseScope firstScope = MainWindowViewModelTestFactory.GetComposition(first).DatabaseScope;
        ApplicationDatabaseScope secondScope = MainWindowViewModelTestFactory.GetComposition(second).DatabaseScope;
        try
        {
            string path = Path.Combine(root, "other.db");
            using (secondScope.OpenProviderConnection(path)) { }
            using SQLiteConnectionEx read = secondScope.OpenReadOnly(path);
            Assert.AreEqual(1, secondScope.ActiveConnectionCount);
            Assert.AreEqual(0, firstScope.ActiveConnectionCount);

            ShellShutdownWorkflowCompletionReceipt receipt = await ShellShutdownWorkflowOwnerTests.CreateDirectOwner(first).RequestWindowCloseAsync();

            Assert.IsTrue(receipt.PreparationSucceeded);
            Assert.AreEqual(1, secondScope.ActiveConnectionCount);
            Assert.AreEqual(1, read.ExecuteScalar<int>("SELECT 1;"));
        }
        finally
        {
            first.SettingDialog.Dispose();
            second.SettingDialog.Dispose();
            Directory.Delete(root, true);
        }
    }

    /// <summary>同compositionの実readonly接続を保持する間はCloseを完了せず、実Disposeで最後の接続が閉じた後だけ完了します。</summary>
    /// <remarks>一般readonly factoryを通し、終了側のsqliteConnections待機開始をClose完了と競合して観測します。song書込みgateだけのdrainではschema/外部player読取りの寿命を回収できないことを検出します。</remarks>
    [TestMethod]
    public async Task WindowClose_WaitsUntilItsReadOnlyConnectionIsDisposed()
    {
        string root = CreateTemporaryDirectory();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ApplicationDatabaseScope scope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
        var connectionWaitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
        BeMusicSeeker.Models.LR2.SQLiteConnectionEx? read = null;
        try
        {
            string path = Path.Combine(root, "owned.db");
            using (scope.OpenProviderConnection(path)) { }
            read = scope.OpenReadOnly(path);
            Task idle = scope.WaitForConnectionsIdleAsync();
            close = ShellShutdownWorkflowOwnerTests.CreateDirectOwner(viewModel, logShutdown: message =>
            {
                if (message == "wait_start target=sqliteConnections") { connectionWaitEntered.TrySetResult(); }
            }).RequestWindowCloseAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(connectionWaitEntered.Task, close, "owned-readonly-shutdown-wait");

            Assert.AreEqual(1, scope.ActiveConnectionCount);
            Assert.IsFalse(idle.IsCompleted);
            Assert.IsFalse(close.IsCompleted);
            read.Dispose();
            read = null;
            await idle;
            ShellShutdownWorkflowCompletionReceipt receipt = await close;
            Assert.AreEqual(0, scope.ActiveConnectionCount);
            Assert.IsTrue(receipt.PreparationSucceeded);
        }
        finally
        {
            read?.Dispose();
            try { if (close != null) { await close; } }
            finally { viewModel.SettingDialog.Dispose(); Directory.Delete(root, true); }
        }
    }

    /// <summary>過去のclose失敗はactiveが0でも更新用準備を失敗Taskにし、通常Closeのreceiptも準備成功へ読み替えません。</summary>
    /// <remarks>leaseのclose境界で故障させます。更新起動のAbort・Failed・失敗通知・終了はStartupUpdateWorkflowOwnerTestsの準備失敗ケースが分担します。</remarks>
    [TestMethod]
    public async Task CloseFailure_RemainsFailureForUpdatePreparationAndWindowCloseAfterIdle()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ApplicationDatabaseScope scope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
        try
        {
            var failure = new IOException("owned close failed");
            ApplicationDatabaseScope.ConnectionLease lease = scope.AcquireConnectionLease();
            Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => lease.Close(() => throw failure)));
            Assert.AreEqual(0, scope.ActiveConnectionCount);
            Assert.AreEqual(1, scope.SqliteCloseFailureCount);
            Assert.IsTrue(scope.WaitForConnectionsIdleAsync().IsCompleted);
            ShellShutdownWorkflowOwner owner = ShellShutdownWorkflowOwnerTests.CreateDirectOwner(viewModel);

            IOException preparationFailure = await Assert.ThrowsExactlyAsync<IOException>(() => owner.PrepareForStartupUpdateAsync("update"));
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.DatabaseConnectionCloseFailed, preparationFailure.Message);
            Assert.IsTrue(owner.ConsumeUpdatePreparationFailure());
            ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();
            Assert.IsFalse(receipt.PreparationSucceeded);
            Assert.IsInstanceOfType<IOException>(receipt.Exception);
            Assert.AreEqual(1, scope.SqliteCloseFailureCount);
        }
        finally { viewModel.SettingDialog.Dispose(); }
    }

    /// <summary>実metadataのATTACH中と接続解放後のarchive前を別々に保持し、Closeが接続と受理済みimporter Taskの両終端を待つことを確認します。</summary>
    /// <remarks>gateway初期化と既存診断でimporterの入場を観測し、接続解放後は終了側のstartupBackgroundTasks待機開始をClose完了と競合して観測します。実startupの入力隔離はStartupMetadata_ConsumesOnlyProfileBundleAndPreservesBaseAndAssetsが分担します。</remarks>
    [TestMethod]
    public async Task WindowClose_JoinsMetadataAttachmentAndImporterArchiveTerminal()
    {
        string root = CreateTemporaryDirectory();
        string profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ApplicationDatabaseScope scope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
        StartupBackgroundTaskSchedulerOwner scheduler = GetStartupScheduler(viewModel);
        var schedulerWaitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellShutdownWorkflowOwner owner = ShellShutdownWorkflowOwnerTests.CreateDirectOwner(viewModel, logShutdown: message =>
        {
            if (message == "wait_start target=startupBackgroundTasks") { schedulerWaitEntered.TrySetResult(); }
        });
        using var releaseAttachment = new ManualResetEventSlim();
        using var releaseArchive = new ManualResetEventSlim();
        var attachmentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
        bool queued = false;
        try
        {
            string sha = new('1', 64);
            string bundle = CreateChartInfoMetadataBundle(scope, profile, [CreateChartInfoRow(sha, new string('a', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string input = Path.Combine(profile, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            File.Move(bundle, input);
            byte[] bundleBytes = File.ReadAllBytes(input);
            string song = Path.Combine(profile, "song.db");
            int attachmentGateUsed = 0;
            var gateway = new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), song,
                initializeSongConnection: connection =>
                {
                    bool attachmentIssued = false;
                    connection.Trace = true;
                    connection.Tracer = sql =>
                    {
                        if (sql.Contains("ATTACH DATABASE", StringComparison.OrdinalIgnoreCase)) { attachmentIssued = true; }
                        else if (attachmentIssued && sql.Contains("SAVEPOINT", StringComparison.OrdinalIgnoreCase)
                            && Interlocked.Exchange(ref attachmentGateUsed, 1) == 0)
                        {
                            // TracerはSQL実行前に呼ばれる。次のSAVEPOINTで止め、直前のATTACH実成功を同接続で確認します。
                            connection.Trace = false;
                            Assert.AreEqual(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_database_list WHERE name = 'bundle';"));
                            attachmentEntered.TrySetResult();
                            releaseAttachment.Wait();
                            connection.Trace = true;
                        }
                    };
                });
            scheduler.Reset(false);
            queued = scheduler.Queue("chart_info_metadata_import", "scope_test", null, () =>
            {
                try
                {
                    ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(profile, gateway, message =>
                    {
                        if (message.StartsWith("chart_info_metadata_import done", StringComparison.Ordinal))
                        {
                            archiveEntered.TrySetResult();
                            releaseArchive.Wait();
                        }
                    });
                    importerCompleted.TrySetResult();
                    return Task.CompletedTask;
                }
                catch (Exception exception) { importerCompleted.TrySetException(exception); throw; }
            });
            Assert.IsTrue(queued);
            scheduler.MarkRequiredInitializationSchedulingComplete();
            scheduler.MarkPostInitializationSchedulingComplete();
            scheduler.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(attachmentEntered.Task, importerCompleted.Task, "metadata-attached");
            Assert.AreEqual(1, scope.ActiveConnectionCount);
            close = owner.RequestWindowCloseAsync();
            Assert.IsFalse(close.IsCompleted);

            releaseAttachment.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(archiveEntered.Task, importerCompleted.Task, "metadata-before-archive");
            await TestUiDispatcherHost.AwaitNotificationAsync(schedulerWaitEntered.Task, close, "metadata-owned-task-shutdown-wait");
            Assert.AreEqual(0, scope.ActiveConnectionCount);
            Assert.IsTrue(scope.WaitForConnectionsIdleAsync().IsCompleted);
            Assert.IsFalse(importerCompleted.Task.IsCompleted);
            Assert.IsFalse(close.IsCompleted, "接続解放だけで受理済みimporterのarchiveと実Task終端を切り離しません。");
            releaseArchive.Set();
            await importerCompleted.Task;
            Assert.IsTrue((await close).PreparationSucceeded);
            string archived = Path.Combine(profile, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            Assert.IsFalse(File.Exists(input));
            CollectionAssert.AreEqual(bundleBytes, File.ReadAllBytes(archived));
            using (SQLiteConnectionEx read = scope.OpenReadOnly(archived))
            {
                Assert.AreEqual(1, read.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", sha));
            }
            string moved = Path.Combine(profile, "reopened-and-moved.db");
            File.Move(archived, moved);
            Assert.IsTrue(File.Exists(moved));
        }
        finally
        {
            releaseAttachment.Set(); releaseArchive.Set();
            try
            {
                if (queued) { await importerCompleted.Task; }
            }
            finally
            {
                try { await (close ?? owner.RequestWindowCloseAsync()); }
                finally { viewModel.SettingDialog.Dispose(); Directory.Delete(root, true); }
            }
        }
    }

    /// <summary>実BMSLibrary startupはprofile内bundleだけを取り込み同profileへarchiveし、異なるbase/assetsの入力集合と全bytesを保全します。</summary>
    /// <remarks>通常のprofile=実行体配置はApplicationPathPolicyTestsへ分担し、このcaseは所有temp内で三つの配置を分離します。</remarks>
    [TestMethod]
    public async Task StartupMetadata_ConsumesOnlyProfileBundleAndPreservesBaseAndAssets()
    {
        string root = CreateTemporaryDirectory();
        string baseDirectory = Directory.CreateDirectory(Path.Combine(root, "base")).FullName;
        string assets = Directory.CreateDirectory(Path.Combine(root, "assets")).FullName;
        string profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
        string songs = Directory.CreateDirectory(Path.Combine(profile, "Songs")).FullName;
        var paths = ApplicationPathSnapshot.FromLayout(Path.Combine(baseDirectory, "BeMusicSeeker.exe"),
            assets, profile, Path.Combine(assets, "lang"), Path.Combine(root, "temp"), Path.Combine(root, "legacy"));
        Settings settings = TestBmsFactory.CreateRootSettings([songs]);
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(paths),
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
        ApplicationDatabaseScope scope = composition.DatabaseScope;
        ShellShutdownWorkflowOwner owner = ShellShutdownWorkflowOwnerTests.CreateDirectOwner(viewModel);
        Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
        try
        {
            string profileSha = new('2', 64), baseSha = new('3', 64), assetsSha = new('4', 64);
            SeedBundle(profile, profileSha, 'b');
            SeedBundle(baseDirectory, baseSha, 'c');
            SeedBundle(assets, assetsSha, 'd');
            File.WriteAllText(Path.Combine(baseDirectory, "keep.txt"), "base preservation marker");
            File.WriteAllText(Path.Combine(assets, "keep.txt"), "assets preservation marker");
            Dictionary<string, byte[]> baseBefore = SnapshotFiles(baseDirectory);
            Dictionary<string, byte[]> assetsBefore = SnapshotFiles(assets);
            string profileInput = Path.Combine(profile, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            byte[] expectedArchive = File.ReadAllBytes(profileInput);
            Directory.CreateDirectory(paths.DataDirectoryPath);
            StartupLibraryConstructionTestSupport.CreateSongDatabase(scope, paths.StandaloneSongDbPath);
            var library = new TestBmsLibrary(scope, paths.StandaloneSongDbPath, null, null, null,
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), paths, settings: settings,
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            StartupBackgroundTaskSchedulerOwner scheduler = GetStartupScheduler(viewModel);
            scheduler.Reset(false);
            library.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => scheduler.Queue(name, reason, dependency, work);
            owner.AttachLibrary(library);

            library.InitializeStartup(null);
            scheduler.MarkRequiredInitializationSchedulingComplete();
            scheduler.MarkPostInitializationSchedulingComplete();
            scheduler.Start();
            close = owner.RequestWindowCloseAsync();
            Assert.IsTrue((await close).PreparationSucceeded);
            using (LR2SongDBExtended read = scope.OpenSongReadOnly(paths.StandaloneSongDbPath))
            {
                CollectionAssert.AreEquivalent(new[] { profileSha }, read.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info;").Select(row => row.sha256).ToArray());
            }
            Assert.IsFalse(File.Exists(profileInput));
            string archived = Path.Combine(profile, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            CollectionAssert.AreEqual(expectedArchive, File.ReadAllBytes(archived));
            using (SQLiteConnectionEx read = scope.OpenReadOnly(archived))
            {
                Assert.AreEqual(profileSha, read.ExecuteScalar<string>("SELECT sha256 FROM chart_info;"));
            }
            File.Move(archived, Path.Combine(profile, "verified-archive.db"));
            AssertFilesUnchanged(baseBefore, baseDirectory);
            AssertFilesUnchanged(assetsBefore, assets);

            void SeedBundle(string directory, string sha, char md5)
            {
                string generated = CreateChartInfoMetadataBundle(scope, directory,
                    [CreateChartInfoRow(sha, new string(md5, 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
                File.Move(generated, Path.Combine(directory, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName));
            }
        }
        finally
        {
            try { await (close ?? owner.RequestWindowCloseAsync()); }
            finally { viewModel.SettingDialog.Dispose(); Directory.Delete(root, true); }
        }
    }

    /// <summary>実Shellと同じ既存schedulerを観測し、受理したTaskの寿命を終了待機へ接続します。fieldの差替えは行いません。</summary>
    private static StartupBackgroundTaskSchedulerOwner GetStartupScheduler(MainWindowViewModel viewModel)
        => typeof(MainWindowViewModel).GetField("startupBackgroundTaskScheduler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(viewModel) as StartupBackgroundTaskSchedulerOwner
            ?? throw new InvalidOperationException("実MainWindowのstartup schedulerを取得できません。");

    /// <summary>保全対象の相対ファイル集合とbytesを、実行前の独立した期待値として捕捉します。</summary>
    private static Dictionary<string, byte[]> SnapshotFiles(string directory)
        => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>実行前後の全ファイル集合とbytesを照合し、同名markerだけを残す破壊や別bundleの移動も検出します。</summary>
    private static void AssertFilesUnchanged(Dictionary<string, byte[]> expected, string directory)
    {
        Dictionary<string, byte[]> actual = SnapshotFiles(directory);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach ((string path, byte[] bytes) in expected) { CollectionAssert.AreEqual(bytes, actual[path], path); }
    }

    /// <summary>このcaseだけが所有するDB入力を、既存profileと分けた一時ディレクトリへ配置します。</summary>
    private static string CreateTemporaryDirectory()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ShellDatabaseScope-" + Guid.NewGuid().ToString("N"))).FullName;
}
