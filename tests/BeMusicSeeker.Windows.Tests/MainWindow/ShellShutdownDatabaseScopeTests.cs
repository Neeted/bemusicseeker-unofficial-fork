using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


/// <summary>実compositionの終了準備が、同じDB所有者の接続寿命と累積close失敗だけを参照することを検査します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class ShellShutdownDatabaseScopeTests
{
    private TestWorkspace? ownedCaseWorkspace;

    /// <summary>返却されたfixtureの終端まで、ケースが専有媒体の親領域を保持します。</summary>
    private TestWorkspace CaseWorkspace => ownedCaseWorkspace ?? throw new InvalidOperationException("ケースの媒体は初期化後に使用します。");

    /// <summary>ケース専有領域を開始し、既存の相対pathの存在条件を維持します。</summary>
    [TestInitialize]
    public void InitializeCaseWorkspace() => ownedCaseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース本文のfinallyとfixtureの終端後に外側の媒体を回収します。</summary>
    [TestCleanup]
    public void CleanupCaseWorkspace() => ownedCaseWorkspace?.Dispose();

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

            ShellShutdownWorkflowCompletionReceipt receipt = await ShellShutdownTestSupport.CreateDirectOwner(first).RequestWindowCloseAsync();

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
            close = ShellShutdownTestSupport.CreateDirectOwner(viewModel, logShutdown: message =>
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
            ShellShutdownWorkflowOwner owner = ShellShutdownTestSupport.CreateDirectOwner(viewModel);

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

    /// <summary>このcaseだけが所有するDB入力を、既存profileと分けた一時ディレクトリへ配置します。</summary>
    private string CreateTemporaryDirectory()
        => Directory.CreateDirectory(Path.Combine(CaseWorkspace.RootDirectory, "ShellDatabaseScope-" + Guid.NewGuid().ToString("N"))).FullName;

}
