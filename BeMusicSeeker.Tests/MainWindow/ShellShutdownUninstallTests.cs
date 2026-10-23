using System;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class ShellShutdownWorkflowOwnerTests
{
    /// <summary>実起動からIR通信中・保存L待ちで撤去終了を要求し、通信・受付の真の終端前にはdropせず、終了後に新規接続でschema不在を確認します。</summary>
    /// <remarks>LR2・未知schemaの保全はLR2SongDBExtendedUninstallTestsに分担し、ここでは停止したworkerによる再作成を検出します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Uninstall_WaitsForActualIrAndAdmissionBeforeDroppingSchema(bool waitingForAdmission)
    {
        await using var fixture = new BmsLibraryIrStartupTests.StartupFixture(waitForCancellation: false, failFirstRequest: false);
        await fixture.InitializeAsync();
        await fixture.Client.Started.Task;
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var dialogs = new FileDbReportRecordingDialogs();
        var store = new ActualUninstallStore();
        var uninstall = new ApplicationDataUninstallWorkflowOwner(dialogs, store);
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, restartFailureDialogs: dialogs);
        owner.AttachLibrary(fixture.Library);
        Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable admission));
        Task<ShellShutdownWorkflowCompletionReceipt>? preparation = null;
        try
        {
            if (waitingForAdmission)
            {
                fixture.Client.Release.TrySetResult();
                await fixture.Client.Completed.Task;
                Assert.IsTrue(fixture.Library.RankingRefreshRunning);
            }
            Assert.IsTrue(await owner.RequestApplicationDataUninstallAsync(uninstall, fixture.SongDbPath));
            Assert.IsFalse(await owner.RequestApplicationDataUninstallAsync(uninstall, fixture.SongDbPath));
            preparation = owner.RequestWindowCloseAsync();
            Assert.IsFalse(preparation.IsCompleted);
            Assert.AreEqual(0, store.CallCount);
            if (!waitingForAdmission)
            {
                admission.Dispose();
                Assert.IsFalse(preparation.IsCompleted, "通信が取消しを観測しても実終端までは削除しません。");
                fixture.Client.Release.TrySetResult();
            }
            else { admission.Dispose(); }
            await preparation;
            await owner.CompleteTerminalShutdownAsync();
            await owner.CompleteTerminalShutdownAsync();
            await fixture.WaitForWorkersAsync();
            Assert.AreEqual(1, store.CallCount);
            using var read = new LR2SongDBExtended(fixture.SongDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: true);
            Assert.AreEqual(0, read.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ir_score','ir_score_refresh_metadata','playlist');"));
        }
        finally
        {
            admission.Dispose(); fixture.Client.Release.TrySetResult();
            if (preparation != null) { await preparation; }
            await fixture.WaitForWorkersAsync();
            viewModel.SettingDialog.Dispose();
        }
    }

    /// <summary>撤去失敗の通知が完了するまで終了せず、表示不能・通知例外でも停止済みownerを再開せず一回だけ終了します。</summary>
    [DataTestMethod]
    [DataRow("shown")]
    [DataRow("unshown")]
    [DataRow("throw")]
    public async Task Uninstall_FailureNotificationCompletesBeforeExitEvenWhenUnavailable(string notification)
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FileDbReportRecordingDialogs { MessageHandler = _ => { entered.TrySetResult(); return release.Task; } };
        int exits = 0;
        var uninstall = new ApplicationDataUninstallWorkflowOwner(dialogs, new FailingUninstallStore());
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, restartFailureDialogs: dialogs, requestApplicationShutdown: () => exits++);
        Task? terminal = null;
        try
        {
            Assert.IsTrue(await owner.RequestApplicationDataUninstallAsync(uninstall, "captured.db"));
            await owner.RequestWindowCloseAsync();
            terminal = FinishAsync();
            await entered.Task;
            Assert.IsFalse(terminal.IsCompleted);
            Assert.AreEqual(0, exits);
            if (notification == "throw") { release.TrySetException(new InvalidOperationException("notification failed")); }
            else { release.TrySetResult(notification == "unshown" ? UiDialogResult.Failed(new InvalidOperationException("unshown")) : UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK)); }
            await terminal;
            owner.RequestTerminalApplicationShutdown();
            Assert.AreEqual(1, exits);
            Assert.IsTrue(owner.IsShutdownRequested);
        }
        finally { release.TrySetResult(UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK)); if (terminal != null) { await terminal; } viewModel.SettingDialog.Dispose(); }

        async Task FinishAsync() { await owner.CompleteTerminalShutdownAsync(); owner.RequestTerminalApplicationShutdown(); }
    }

    private sealed class ActualUninstallStore : IApplicationDataUninstallStore
    {
        internal int CallCount { get; private set; }
        public void Uninstall(string songDbPath) { CallCount++; new Lr2ApplicationDataUninstallStore().Uninstall(songDbPath); }
    }
    private sealed class FailingUninstallStore : IApplicationDataUninstallStore
    {
        public void Uninstall(string songDbPath) => throw new IOException("store failed");
    }
}
