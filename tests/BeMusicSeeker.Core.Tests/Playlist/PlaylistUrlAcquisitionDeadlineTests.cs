using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;

namespace BeMusicSeeker.Tests;


public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実HTTPで単発本文を取り消し、fixtureの終了処理より前にpeer切断と要求の実終端を観測します。</summary>
    [TestMethod]
    public async Task SingleBodyCancellation_DisconnectsRealPeerBeforeFixtureDisposal()
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        List<Task> httpRequests = ownedHttpTasks;
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); ownedHttpTasks.Add(ownedServer.Completion); return ownedServer; }

        Exception? httpPrimaryFailure = null;
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        SingleRequestHttpServer? server = null;

        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistRealCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PlaylistWorkspaceViewModel? workspace = null;
        Task? request = null;
        try
        {
            server = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, [1, 2, 3, 4], holdBody: true));
            var gateway = new BodyGateway(root, Stream.Null) { RealHttp = true };
            int browsers = 0;
            workspace = CreateWorkspace(databaseScope, action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), browserSink: _ => browsers++);
            request = workspace.RunSinglePlaylistUrlAsync(new Uri(server.Address, "file.zip"));
            httpRequests.Add(request);
            await server.WaitForPhaseAsync(server.BodyPrefixSent.Task, "URL body prefix", request);
            workspace.CancelPlaylistUrlDownload();
            Task cancelled = SingleRequestHttpServer.AssertClientCancelledAsync(request);
            await SingleRequestHttpServer.AwaitOperationAsync(cancelled, httpRequests, httpOperation.Token);
            await server.WaitForPhaseAsync(server.ClientDisconnected.Task, "URL peer disconnect before disposal", cancelled);
            Assert.AreEqual(0, browsers);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        catch (Exception httpPrimary) { httpPrimaryFailure = httpPrimary; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            workspace?.RequestPlaylistUrlShutdown();
            if (workspace != null) { httpRequests.Add(workspace.WaitForPlaylistUrlIdleAsync()); }
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpPrimaryFailure, httpRequests.Concat(new Task?[] { request }.OfType<Task>().Where(task => !task.IsCompleted)), httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {

                httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(root, true); migrationWorkspace.Dispose();

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (httpPrimaryFailure != null) { throw new AggregateException(httpPrimaryFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    /// <summary>通常失敗後のブラウザー配送がDispatcher待ちの間に終了した場合も、配送直前の取消確認でブラウザーを開きません。</summary>
    [TestMethod]
    public async Task BrowserFallback_DeferredPresentationObservesShutdown()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistDeferredFallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new RecordingPlaylistUrlDownloadGateway(root) { TransportException = new IOException("normal network failure") };
        int browsers = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
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

    /// <summary>受信ごとに無受信30秒を更新し、期限取消しの観測後も実read終端まで応答・一時入力を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BodyDeadline_ResetsOnDataAndJoinsCancelledRead(bool sharedHtml)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistBodyDeadline-" + Guid.NewGuid().ToString("N"));
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
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistBodyWrite-" + Guid.NewGuid().ToString("N"));
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
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistSingleCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stream = new StagedBodyStream();
        var gateway = new BodyGateway(root, stream);
        int browsers = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
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
