using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class StartupLibraryInitializationWorkflowOwnerTests
{
    /// <summary>保留復元失敗後の選択条件を実復元ステージで検証します。Retryはfresh取得、Resetは全登録削除、削除失敗は再承認、閉鎖は失敗、Exitだけが終了要求です。</summary>
    /// <remarks>表示応答だけを代替し、実SQLite・ファイルreadlock・実判断主体を使います。DB/モデル保護外の待機と実VM接続は代表フロー、実ボタンはWindowテストへ分担します。DB・設定・入力・モデル・表示応答はケースごとに所有し、共有Windowや通知購読を差し替えません。</remarks>
    [DataTestMethod]
    [DataRow("retry")]
    [DataRow("reset")]
    [DataRow("reset-failure")]
    [DataRow("close")]
    [DataRow("escape")]
    [DataRow("exit")]
    public async Task PendingRestore_ExplicitChoicePreservesStageContract(string scenario)
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        using var input = new PendingRestoreInput();
        input.Register(scope.SongDbPath);
        Settings settings = TestBmsFactory.CreateRootSettings([]);
        var options = BmsLibraryOptionsSnapshot.CreateCurrent(settings);
        var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, null,
            new TestUiScheduler(() => null), () => options, settings: settings);
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        using StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(library.OperationAdmission,
            library.Lr2Synchronization.PlaylistOperationAdmission);
        var gateway = new BmsLibraryDbGateway(scope.SongDbPath);
        if (scenario == "reset-failure") { input.BlockReset(scope.SongDbPath); }
        string originalHash = ChartFileContentReader.ReadSnapshot(input.A).Sha256;
        FileStream? held = input.LockB();
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs();
        dialogs.PendingRestoreObservedAsync = () =>
        {
            Assert.AreEqual(3, gateway.LoadInstallPackages().Count, "復元失敗・削除失敗のどちらも、正常不存在を含む全行を保持します。");
            Assert.AreEqual(0, library.ChartPackagesPending.Count, "完成した部分だけを公開しません。");
            if (scenario == "retry")
            {
                File.WriteAllText(input.A, PendingRestoreInput.Chart("Changed A"));
                held?.Dispose(); held = null;
            }
            if (scenario == "reset-failure" && dialogs.PendingRestoreCount == 2)
            { ExecuteSongDbFixtureTransaction(scope.SongDbPath, db => db.Execute("DROP TRIGGER pending_reset_fail")); }
            PendingPackageRestoreChoice choice = scenario switch
            {
                "retry" => PendingPackageRestoreChoice.Retry,
                "reset" or "reset-failure" => PendingPackageRestoreChoice.Reset,
                "exit" => PendingPackageRestoreChoice.Exit,
                _ => PendingPackageRestoreChoice.None
            };
            UiDialogStatus status = scenario == "close" ? UiDialogStatus.ClosedByUser
                : scenario == "escape" ? UiDialogStatus.CancelledByUser : UiDialogStatus.Accepted;
            return Task.FromResult(new UiWindowDialogResult<PendingPackageRestoreChoice>(status, choice));
        };
        try
        {
            Task<bool> operation = owner.RestorePendingPackagesAsync(library, accepted.Capability, options, dialogs);
            if (scenario is "close" or "escape")
            {
                PendingPackageRestoreException failure = await Assert.ThrowsExceptionAsync<PendingPackageRestoreException>(() => operation);
                Assert.IsInstanceOfType<IOException>(failure.InnerException);
            }
            else { Assert.AreEqual(scenario == "exit", await operation); }
            Assert.AreEqual(scenario == "reset-failure" ? 2 : 1, dialogs.PendingRestoreCount);
            Assert.AreEqual(scenario is "reset" or "reset-failure" ? 0 : scenario == "retry" ? 2 : 3, gateway.LoadInstallPackages().Count);
            Assert.AreEqual(scenario == "retry" ? 2 : 0, library.ChartPackagesPending.Count);
            if (scenario == "retry")
            {
                string actual = library.ChartPackagesPending.Single(package => package.path == Path.GetDirectoryName(input.A)).ChartEntries.Single().Chart.Sha256;
                Assert.AreNotEqual(originalHash, actual);
                Assert.AreEqual(ChartFileContentReader.ReadSnapshot(input.A).Sha256, actual);
            }
            Assert.IsTrue(File.Exists(input.A)); Assert.IsTrue(File.Exists(input.B));
        }
        finally { held?.Dispose(); library.RequestShutdown("pending-stage-test"); }
    }

    /// <summary>保留DB確定後の通知故障は元原因の通常失敗として返し、三択やReadyへ進みません。正常不存在の整理が確定したDBを保持します。</summary>
    /// <remarks>固有DB・入力・設定と実Libraryを使い、通知受信先だけを故障させます。非WPF schedulerで実Actionと故障Taskを回収し、共有Dispatcherの未処理例外を作りません。モデルのロック解放・配送拒否等の組合せは必須通知終端テストへ分担します。</remarks>
    [TestMethod]
    public async Task PendingRestore_PostCommitNotificationFailureDoesNotOfferRecoveryOrPublishReady()
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        using var input = new PendingRestoreInput();
        input.Register(scope.SongDbPath);
        Settings settings = TestBmsFactory.CreateRootSettings([]);
        var options = BmsLibraryOptionsSnapshot.CreateCurrent(settings);
        var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, null,
            new TestUiScheduler(() => null), () => options, settings: settings);
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(library.OperationAdmission,
            library.Lr2Synchronization.PlaylistOperationAdmission);
        Action? scoreNotification = null;
        var expected = new IOException("pending post-commit notification failed");
        int notifications = 0;
        PropertyChangedEventHandler failNotification = (_, args) =>
        {
            if (args.PropertyName != nameof(BMSLibrary.ChartPackagesPending)) { return; }
            notifications++;
            throw expected;
        };
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs();
        try
        {
            LibraryFileInitializationResult files = library.InitializeStartup([], null, PerformanceInteraction.Start("pending-publication-workflow"),
                accepted.Capability, leapYearRepairApproval: new(scope.SongDbPath), repairNotificationObserver: _ => { },
                scoreNotificationObserver: notification => scoreNotification = notification);
            PackageLifecycleOwner lifecycle = typeof(BMSLibrary).GetField("packageLifecycleOwner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(library)
                as PackageLifecycleOwner ?? throw new AssertFailedException("保留準備状態の管理主体が見つかりません。");
            StartupInstallReadinessState readiness = lifecycle.StartupReadiness;
            Assert.IsTrue(readiness.CatalogLoaded);
            Assert.IsTrue(readiness.DestinationResourceIndexReady);
            Assert.IsFalse(readiness.PendingPackagesRestored);
            library.PropertyChanged += failNotification;
            IOException actual = await Assert.ThrowsExceptionAsync<IOException>(() =>
                owner.RestorePendingPackagesAsync(library, accepted.Capability, files.Options, dialogs));
            Assert.AreSame(expected, actual);
            Assert.AreEqual(1, notifications);
            Assert.AreEqual(0, dialogs.PendingRestoreCount);
            Assert.IsFalse(readiness.PendingPackagesRestored);
            Assert.IsFalse(readiness.InstallReady);
            Assert.AreEqual(2, new BmsLibraryDbGateway(scope.SongDbPath).LoadInstallPackages().Count);
        }
        finally
        {
            library.PropertyChanged -= failNotification;
            accepted.Dispose();
            try { scoreNotification?.Invoke(); }
            finally { library.RequestShutdown("pending-publication-workflow"); }
        }
    }

    /// <summary>表示障害とアプリ終了は再選択・Resetへ変換しません。未表示状態や配送例外も元の全登録を保ち、復元だけを再実行しません。</summary>
    /// <remarks>実復元が未確定で失敗した後の表示境界だけに故障を置きます。固有DB・設定・入力とWindowを作らない表示代替を使い、共有表示状態を変更しません。確定後のモデル通知障害は、モデルの必須通知終端テストと実VMの代表接続へ分担します。</remarks>
    [DataTestMethod]
    [DataRow("failed")]
    [DataRow("unavailable")]
    [DataRow("appclosing")]
    [DataRow("throw")]
    [DataRow("shutdown")]
    [DataRow("already-closing")]
    public async Task PendingRestore_DialogAndShutdownFailuresDoNotAuthorizeMutation(string scenario)
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        using var input = new PendingRestoreInput();
        input.Register(scope.SongDbPath);
        Settings settings = TestBmsFactory.CreateRootSettings([]);
        var options = BmsLibraryOptionsSnapshot.CreateCurrent(settings);
        var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, null,
            new TestUiScheduler(() => null), () => options, settings: settings);
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        using StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(library.OperationAdmission,
            library.Lr2Synchronization.PlaylistOperationAdmission);
        using FileStream held = input.LockB();
        var expected = new IOException("dialog delivery failed");
        bool shutdown = scenario == "already-closing";
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs
        {
            PendingRestoreObservedAsync = () =>
            {
                if (scenario == "throw") { throw expected; }
                shutdown = scenario == "shutdown";
                UiDialogStatus status = scenario switch
                {
                    "failed" => UiDialogStatus.Failed,
                    "unavailable" => UiDialogStatus.OwnerUnavailable,
                    "appclosing" => UiDialogStatus.AppClosing,
                    _ => UiDialogStatus.Accepted
                };
                return Task.FromResult(new UiWindowDialogResult<PendingPackageRestoreChoice>(status, PendingPackageRestoreChoice.Reset, error: expected));
            }
        };
        try
        {
            Task<bool> operation = owner.RestorePendingPackagesAsync(library, accepted.Capability, options, dialogs, () => shutdown);
            if (scenario is "shutdown" or "already-closing") { await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation); }
            else if (scenario == "throw") { Assert.AreSame(expected, await Assert.ThrowsExceptionAsync<IOException>(() => operation)); }
            else
            {
                InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => operation);
                Assert.AreSame(expected, failure.InnerException);
            }
            Assert.AreEqual(scenario == "already-closing" ? 0 : 1, dialogs.PendingRestoreCount);
            Assert.AreEqual(3, new BmsLibraryDbGateway(scope.SongDbPath).LoadInstallPackages().Count);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
        }
        finally { library.RequestShutdown("pending-dialog-failure-test"); }
    }
}

/// <summary>登録ルート外に正常・読取不能・正常不存在を持つ専用入力です。通常一時領域の全体回収に依存しません。</summary>
internal sealed class PendingRestoreInput : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PendingRestoreUi-" + Guid.NewGuid().ToString("N"));
    internal string A => Path.Combine(root, "A", "a.bms");
    internal string B => Path.Combine(root, "B", "b.bms");
    internal string Missing => Path.Combine(root, "Missing");
    internal PendingRestoreInput()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(A) ?? throw new InvalidOperationException());
        Directory.CreateDirectory(Path.GetDirectoryName(B) ?? throw new InvalidOperationException());
        File.WriteAllText(A, Chart("Original A")); File.WriteAllText(B, Chart("Original B"));
    }
    internal static string Chart(string title) => "#TITLE " + title + "\n#BPM 120\n#00111:01\n";
    internal FileStream LockB() => new(B, FileMode.Open, FileAccess.Read, FileShare.None);
    internal void Register(string database) => ExecuteSongDbFixtureTransaction(database, db =>
    {
        db.CreateTable<LR2SongDBExtended.install>();
        db.InsertOrReplace(new LR2SongDBExtended.install { path = Path.GetDirectoryName(A) ?? throw new InvalidOperationException(), delete_parent = false });
        db.InsertOrReplace(new LR2SongDBExtended.install { path = Path.GetDirectoryName(B) ?? throw new InvalidOperationException(), delete_parent = true });
        db.InsertOrReplace(new LR2SongDBExtended.install { path = Missing, delete_parent = false });
    });
    internal void BlockReset(string database) => ExecuteSongDbFixtureTransaction(database, db =>
        db.Execute("CREATE TRIGGER pending_reset_fail AFTER DELETE ON install WHEN (SELECT COUNT(*) FROM install) = 0 BEGIN SELECT RAISE(ABORT, 'pending-reset-failure'); END"));
    public void Dispose() => Directory.Delete(root, recursive: true);
}
