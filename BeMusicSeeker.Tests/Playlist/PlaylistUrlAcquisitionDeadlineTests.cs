using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    /// <summary>URLの未受理cleanupが失敗しても、独立した実再走査の終端前にClose・撤去へ進まず、元要求の故障と終了診断を保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task WindowClose_UrlCleanupFailureStillJoinsIndependentWorker(bool uninstall)
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistFailedShutdownJoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songPath = Path.Combine(root, "song.db");
        using (var setup = new LR2SongDBExtended(songPath)) { }
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("shutdown-cleanup-failure-marker");
        var events = new ConcurrentQueue<string>();
        var library = new TestBmsLibrary(songPath);
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), ownedInstallSink: _ => false, dialogService: dialogs);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = ShellShutdownWorkflowOwnerTests.CreateDirectOwner(viewModel,
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
                    new OrderedShutdownUninstallStore(events)), songPath));
            }
            Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
            terminal = CompleteCloseAsync(close);
            Assert.IsFalse(close.IsCompleted);
            releaseCleanup.TrySetResult();
            AggregateException failure = await Assert.ThrowsExceptionAsync<AggregateException>(() => request);
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

    private sealed class OrderedShutdownUninstallStore(ConcurrentQueue<string> events) : IApplicationDataUninstallStore
    {
        public void Uninstall(string songDbPath)
        {
            events.Enqueue("drop");
            new Lr2ApplicationDataUninstallStore().Uninstall(songDbPath);
        }
    }

    /// <summary>実HTTPで単発本文を取り消し、fixtureの終了処理より前にpeer切断と要求の実終端を観測します。</summary>
    [TestMethod]
    public async Task SingleBodyCancellation_DisconnectsRealPeerBeforeFixtureDisposal()
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistRealCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new SingleRequestHttpServer([1, 2, 3, 4], holdBody: true);
        var gateway = new BodyGateway(root, Stream.Null) { RealHttp = true };
        int browsers = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), browserSink: _ => browsers++);
        Task request = workspace.RunSinglePlaylistUrlAsync(new Uri(server.Address, "file.zip"));
        try
        {
            await server.WaitForPhaseAsync(server.BodyPrefixSent.Task, "URL body prefix");
            workspace.CancelPlaylistUrlDownload();
            try { await request; Assert.Fail("取消しを成功へ変換しません。"); } catch (OperationCanceledException) { }
            await server.WaitForPhaseAsync(server.ClientDisconnected.Task, "URL peer disconnect before disposal");
            Assert.AreEqual(0, browsers);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally
        {
            workspace.RequestPlaylistUrlShutdown(); server.ReleaseBody.TrySetResult();
            try { await request; } catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }

    /// <summary>通常失敗後のブラウザー配送がDispatcher待ちの間に終了した場合も、配送直前の取消確認でブラウザーを開きません。</summary>
    [TestMethod]
    public async Task BrowserFallback_DeferredPresentationObservesShutdown()
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistDeferredFallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new RecordingPlaylistUrlDownloadGateway(root) { TransportException = new IOException("normal network failure") };
        int browsers = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), browserSink: _ => browsers++,
            presentationScheduler: async action => { entered.TrySetResult(); await release.Task; action(); });
        Task request = workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/file.zip"));
        try
        {
            await entered.Task;
            workspace.RequestPlaylistUrlShutdown();
            Task idle = workspace.WaitForPlaylistUrlIdleAsync();
            Assert.IsFalse(idle.IsCompleted);
            release.TrySetResult();
            try { await request; Assert.Fail("終了取消しをfallback成功へ変換しません。"); } catch (OperationCanceledException) { }
            try { await idle; } catch (OperationCanceledException) { }
            Assert.AreEqual(0, browsers);
        }
        finally { release.TrySetResult(); try { await request; } catch (OperationCanceledException) { } Directory.Delete(root, true); }
    }

    /// <summary>先行取得の未受理cleanup中に次の取得を開始でき、Closeは両方の実Taskを回収してから完了します。</summary>
    [TestMethod]
    public async Task WindowClose_JoinsEarlierCleanupAndLaterBodyIndependently()
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistDoubleJoin-" + Guid.NewGuid().ToString("N"));
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), ownedInstallSink: _ => false,
            dialogService: new RecordingPlaylistUrlDialogService());
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = ShellShutdownWorkflowOwnerTests.CreateDirectOwner(viewModel, playlistWorkspace: workspace);
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
            releaseCleanup.TrySetResult();
            await first;
            Assert.IsFalse(close.IsCompleted, "先行cleanupだけの解放で後続readを切り離しません。");
            body.Terminal.TrySetResult();
            try { await second; } catch (OperationCanceledException) { }
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

    /// <summary>受信ごとに無受信30秒を更新し、期限取消しの観測後も実read終端まで応答・一時入力を保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BodyDeadline_ResetsOnDataAndJoinsCancelledRead(bool sharedHtml)
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistBodyDeadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new BodyTimeProvider();
        using var stream = new StagedBodyStream();
        var gateway = new BodyGateway(root, stream);
        Uri uri = new(sharedHtml ? "https://www.mediafire.com/file/test/file" : "https://example.invalid/file.zip");
        var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }, clock);
        Task<PlaylistUrlDownloadResult> request = workflow.DownloadCandidateAsync(uri);
        try
        {
            await stream.Started[0].Task;
            clock.Advance(TimeSpan.FromSeconds(29));
            stream.Release[0].TrySetResult(1);
            await stream.Started[1].Task;
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.IsFalse(request.IsCompleted, "総転送時間が30秒を超えても受信中は継続します。");
            stream.Release[1].TrySetResult(1);
            await stream.Started[2].Task;
            clock.Advance(TimeSpan.FromSeconds(30));
            await stream.Cancelled.Task;
            Assert.IsFalse(request.IsCompleted);
            Assert.IsFalse(stream.IsDisposed);
            stream.Terminal.TrySetResult();
            Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, (await request).Kind);
            Assert.IsTrue(stream.IsDisposed);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally
        {
            stream.Terminal.TrySetResult();
            foreach (TaskCompletionSource<int> release in stream.Release) { release.TrySetResult(0); }
            await request;
            Directory.Delete(root, true);
        }
    }

    /// <summary>ローカル書込み待ちが30秒を超えても無受信期限を消費せず、次のreadと保存を成功させます。</summary>
    [TestMethod]
    public async Task BodyDeadline_DoesNotLimitLocalWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistBodyWrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new BodyTimeProvider();
        using var stream = new MemoryStream([1, 2, 3], writable: false);
        var gateway = new BodyGateway(root, stream) { HoldWrite = true };
        Task<PlaylistUrlDownloadResult> request = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }, clock)
            .DownloadCandidateAsync(new Uri("https://example.invalid/file.zip"));
        try
        {
            await gateway.WriteStarted.Task;
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.IsFalse(request.IsCompleted);
            gateway.ReleaseWrite.TrySetResult();
            PlaylistUrlDownloadResult result = await request;
            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(result.FilePath));
        }
        finally { gateway.ReleaseWrite.TrySetResult(); await request; Directory.Delete(root, true); }
    }

    /// <summary>単発取得も実hubから取り消せ、本文tokenが取消しを観測してから実終端まで行を保持し、ブラウザーを開きません。</summary>
    [TestMethod]
    public async Task SingleBodyCancellation_JoinsIoBeforeRemovingProgressAndNeverFallsBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "PlaylistSingleCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stream = new StagedBodyStream();
        var gateway = new BodyGateway(root, stream);
        int browsers = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
            optionsProvider: () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            acquisitionWorkflow: new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), browserSink: _ => browsers++);
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        hub.AttachPlaylistProgressSources(workspace, action => action(), () => false);
        Task request = workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/file.zip"));
        try
        {
            await stream.Started[0].Task;
            Assert.IsTrue(hub.Rows.Single(row => row.Key == "url").CanCancel);
            workspace.CancelPlaylistUrlDownload();
            await stream.Cancelled.Task;
            Assert.IsFalse(request.IsCompleted);
            Assert.IsFalse(hub.Rows.Single(row => row.Key == "url").CanCancel);
            stream.Terminal.TrySetResult();
            try { await request; Assert.Fail("取消しを成功へ変換しません。"); } catch (OperationCanceledException) { }
            Assert.IsFalse(hub.Rows.Any(row => row.Key == "url"));
            Assert.AreEqual(0, browsers);
        }
        finally
        {
            workspace.RequestPlaylistUrlShutdown(); stream.Terminal.TrySetResult();
            foreach (TaskCompletionSource<int> release in stream.Release) { release.TrySetResult(0); }
            try { await request; } catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }

    private sealed class BodyGateway(string root, Stream stream) : IPlaylistUrlDownloadGateway
    {
        internal bool RealHttp { get; init; }
        internal bool HoldWrite { get; init; }
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
            => RealHttp ? AppHttpClient.Shared.OpenReadAsync(uri, cancellationToken) : Task.FromResult(new AppHttpResponse(uri, stream));
        public string GetTemporaryDirectory() => root;
        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share) => new BodyFileStream(path, mode, access, share, this);
        public bool FileExists(string path) => File.Exists(path);
        public void DeleteFile(string path) => File.Delete(path);
        private sealed class BodyFileStream(string path, FileMode mode, FileAccess access, FileShare share, BodyGateway owner) : FileStream(path, mode, access, share)
        {
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (owner.HoldWrite) { owner.WriteStarted.TrySetResult(); await owner.ReleaseWrite.Task.WaitAsync(cancellationToken); }
                await base.WriteAsync(buffer, offset, count, cancellationToken);
            }
        }
    }

    private sealed class StagedBodyStream : Stream
    {
        private int next;
        internal TaskCompletionSource[] Started { get; } = [.. Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        internal TaskCompletionSource<int>[] Release { get; } = [.. Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))];
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int index = next++;
            Started[index].TrySetResult();
            try
            {
                int count = await Release[index].Task.WaitAsync(cancellationToken);
                buffer.Span[..count].Fill((byte)' ');
                return count;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                await Terminal.Task;
                throw;
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BodyTimeProvider : TimeProvider
    {
        private readonly List<BodyTimer> timers = [];
        private TimeSpan elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new BodyTimer(this, callback, state);
            lock (timers) { timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        internal void Advance(TimeSpan amount)
        {
            BodyTimer[] due;
            lock (timers) { elapsed += amount; due = [.. timers.Where(timer => timer.Due <= elapsed)]; foreach (BodyTimer timer in due) { timer.Due = TimeSpan.MaxValue; } }
            foreach (BodyTimer timer in due) { timer.Fire(); }
        }
        private sealed class BodyTimer(BodyTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimeSpan Due { get; set; } = TimeSpan.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.timers) { Due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : owner.elapsed + dueTime; return true; }
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (owner.timers) { owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
