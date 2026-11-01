using System.Collections.Concurrent;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;

namespace BeMusicSeeker.Tests;


public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>URLの未受理cleanupが失敗しても、独立した実再走査の終端前にClose・撤去へ進まず、元要求の故障と終了診断を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task WindowClose_UrlCleanupFailureStillJoinsIndependentWorker(bool uninstall)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistFailedShutdownJoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songPath = Path.Combine(root, "song.db");
        using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(songPath)) { }
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("shutdown-cleanup-failure-marker");
        var events = new ConcurrentQueue<string>();
        var library = new TestBmsLibrary(databaseScope, songPath);
        var maintenance = new MaintenanceRescanWorkflowOwner(
            (_, _, _, _) =>
            {
                workerEntered.TrySetResult();
                releaseWorker.Task.GetAwaiter().GetResult();
                events.Enqueue("worker-terminal");
                return new MaintenanceWorkflowResult();
            },
            action => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            action => action(), dialogs: new RecordingPlaylistUrlDialogService());
        maintenance.AttachLibrary(library);
        Uri uri = new("https://example.invalid/rejected.bms");
        var gateway = new RecordingPlaylistUrlDownloadGateway(root)
        {
            BeforeDelete = () => { cleanupEntered.TrySetResult(); releaseCleanup.Task.GetAwaiter().GetResult(); },
            DeleteFailure = cleanupFailure
        };
        gateway.AddResponse(uri, () => new AppHttpResponse(uri, new MemoryStream([1, 2, 3], writable: false)));
        var dialogs = new RecordingPlaylistUrlDialogService();
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), ownedInstallSink: _ => false, dialogService: dialogs);
        ShellShutdownWorkflowOwner owner = ShellShutdownTestSupport.CreateDirectOwner(viewModel,
            playlistWorkspace: workspace, maintenanceRescanWorkflow: maintenance, restartFailureDialogs: dialogs,
            requestApplicationShutdown: () => events.Enqueue("exit"),
            logShutdownWarning: message => { if (message.Contains("playlist_url_shutdown_failed", StringComparison.Ordinal)) { diagnostic.TrySetResult(message); } });
        owner.AttachLibrary(library);
        Task request = workspace.RunSinglePlaylistUrlAsync(uri);
        Task? terminal = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupEntered.Task, request, "failed URL cleanup entered");
            Assert.IsTrue((await maintenance.RequestStartAsync()).Started);
            await workerEntered.Task;
            if (uninstall)
            {
                Assert.IsTrue(await owner.RequestApplicationDataUninstallAsync(new ApplicationDataUninstallWorkflowOwner(dialogs,
                    new OrderedShutdownUninstallStore(databaseScope, events)), songPath));
            }
            Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
            terminal = CompleteCloseAsync(close);
            Assert.IsFalse(close.IsCompleted);
            releaseCleanup.TrySetResult();
            AggregateException failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => request);
            CollectionAssert.Contains(failure.Flatten().InnerExceptions, cleanupFailure);
            await TestUiDispatcherHost.AwaitNotificationAsync(diagnostic.Task, terminal, "URL shutdown failure diagnostic");
            StringAssert.Contains(await diagnostic.Task, cleanupFailure.Message);
            Assert.IsFalse(close.IsCompleted, "URL故障後も独立workerを待ち続けます。");
            Assert.IsFalse(terminal.IsCompleted);
            CollectionAssert.AreEqual(Array.Empty<string>(), events.ToArray());
            releaseWorker.TrySetResult();
            await terminal;
            CollectionAssert.AreEqual(uninstall ? new[] { "worker-terminal", "drop", "exit" } : new[] { "worker-terminal", "exit" }, events.ToArray());
        }
        finally
        {
            releaseCleanup.TrySetResult(); releaseWorker.TrySetResult();
            try { await request; } catch (AggregateException failure) when (failure.Flatten().InnerExceptions.Contains(cleanupFailure)) { }
            await maintenance.WaitForIdleAsync();
            if (terminal != null) { await terminal; }
            viewModel.SettingDialog.Dispose();
            Directory.Delete(root, true);
        }

        async Task CompleteCloseAsync(Task<ShellShutdownWorkflowCompletionReceipt> close)
        {
            await close;
            await owner.CompleteTerminalShutdownAsync();
            owner.RequestTerminalApplicationShutdown();
        }
    }

    private sealed class OrderedShutdownUninstallStore(ApplicationDatabaseScope databaseScope, ConcurrentQueue<string> events) : IApplicationDataUninstallStore
    {
        public void Uninstall(string songDbPath)
        {
            events.Enqueue("drop");
            new Lr2ApplicationDataUninstallStore(databaseScope).Uninstall(songDbPath);
        }
    }

    /// <summary>先行取得の未受理cleanup中に次の取得を開始でき、どちらを先に解放してもCloseは両方の実Task終端まで完了しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WindowClose_JoinsEarlierCleanupAndLaterBodyIndependently(bool bodyCompletesFirst)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistDoubleJoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new StagedBodyStream();
        Uri firstUri = new("https://example.invalid/first.bms");
        Uri secondUri = new("https://example.invalid/second.bms");
        var gateway = new RecordingPlaylistUrlDownloadGateway(root);
        int deletes = 0;
        gateway.BeforeDelete = () => { if (Interlocked.Increment(ref deletes) == 1) { cleanupEntered.TrySetResult(); releaseCleanup.Task.GetAwaiter().GetResult(); } };
        gateway.AddResponse(firstUri, () => new AppHttpResponse(firstUri, new MemoryStream([1, 2, 3], writable: false)));
        gateway.AddResponse(secondUri, () => new AppHttpResponse(secondUri, body));
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), ownedInstallSink: _ => false,
            dialogService: new RecordingPlaylistUrlDialogService());
        ShellShutdownWorkflowOwner owner = ShellShutdownTestSupport.CreateDirectOwner(viewModel, playlistWorkspace: workspace);
        Task first = workspace.RunSinglePlaylistUrlAsync(firstUri);
        Task? second = null;
        Task? close = null;
        try
        {
            await cleanupEntered.Task;
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            second = workspace.RunSinglePlaylistUrlAsync(secondUri);
            await body.Started[0].Task;
            close = owner.RequestWindowCloseAsync();
            await body.Cancelled.Task;
            Assert.IsFalse(close.IsCompleted);
            if (bodyCompletesFirst)
            {
                body.Terminal.TrySetResult();
                try { await second; } catch (OperationCanceledException) { }
                Assert.IsFalse(close.IsCompleted, "後続readだけの解放で先行cleanupを切り離しません。");
                releaseCleanup.TrySetResult();
                await first;
            }
            else
            {
                releaseCleanup.TrySetResult();
                await first;
                Assert.IsFalse(close.IsCompleted, "先行cleanupだけの解放で後続readを切り離しません。");
                body.Terminal.TrySetResult();
                try { await second; } catch (OperationCanceledException) { }
            }
            await close;
        }
        finally
        {
            releaseCleanup.TrySetResult(); body.Terminal.TrySetResult();
            foreach (TaskCompletionSource<int> release in body.Release) { release.TrySetResult(0); }
            await first;
            if (second != null) { try { await second; } catch (OperationCanceledException) { } }
            if (close != null) { await close; }
            viewModel.SettingDialog.Dispose();
            Directory.Delete(root, true);
        }
    }

}
