using System.Text;
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

    /// <summary>受信ごとに操作開始時の無受信秒数（既定30秒・設定の5秒・600秒）を更新し、期限取消しの観測後も実read終端まで応答・一時入力を保持します。</summary>
    [TestMethod]
    [DataRow(false, 30)]
    [DataRow(true, 30)]
    [DataRow(false, 5)]
    [DataRow(true, 600)]
    public async Task BodyDeadline_ResetsOnDataAndJoinsCancelledRead(bool sharedHtml, int idleSeconds)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistBodyDeadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new ManualRequestTimeProvider();
        using var stream = new StagedBodyStream();
        var gateway = new BodyGateway(root, stream);
        Uri uri = new(sharedHtml ? "https://www.mediafire.com/file/test/file" : "https://example.invalid/file.zip");
        var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }, clock);
        Task<PlaylistUrlDownloadResult> request = workflow.DownloadCandidateAsync(uri,
            options: new PlaylistUrlAcquisitionOptionsSnapshot { IdleTimeout = TimeSpan.FromSeconds(idleSeconds) });
        try
        {
            await stream.Started[0].Task;
            clock.Advance(TimeSpan.FromSeconds(idleSeconds - 1));
            stream.Release[0].TrySetResult(1);
            await stream.Started[1].Task;
            clock.Advance(TimeSpan.FromSeconds(idleSeconds - 1));
            Assert.IsFalse(request.IsCompleted, "総転送時間が無受信秒数を超えても受信中は継続します。");
            stream.Release[1].TrySetResult(1);
            await stream.Started[2].Task;
            clock.Advance(TimeSpan.FromSeconds(idleSeconds));
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
        var clock = new ManualRequestTimeProvider();
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
        public Task<AppHttpResponse> OpenReadAsync(Uri uri, TimeSpan headerTimeout, CancellationToken cancellationToken)
            => RealHttp ? AppHttpClient.Shared.OpenReadAsync(uri, headerTimeout, cancellationToken) : Task.FromResult(new AppHttpResponse(uri, stream));
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

    /// <summary>ヘッダー待ちは操作開始時の設定秒数（既定30秒より短い5秒・長い600秒）で切れ、共有ページを解決した後の2本目の要求にも同じ値を使います。</summary>
    /// <remarks>
    /// workflowから本番のgateway・時計を差し替えた<see cref="AppHttpClient"/>・段階制御のhandlerまで動かし、設定秒数の1ms前は取り消さず、ちょうどで通常失敗にします。
    /// 時間切れを利用者取消にしない、固定の30秒や共有クライアントの期限へ戻る、2本目だけ既定値になる誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(5, false)]
    [DataRow(600, false)]
    [DataRow(5, true)]
    [DataRow(600, true)]
    public async Task HeaderWait_UsesOperationIdleTimeoutThroughProductionGateway(int idleSeconds, bool sharedPageSecondRequest)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistHeaderWait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var clock = new ManualRequestTimeProvider();
        var handler = new StagedResponseHandler();
        Task<PlaylistUrlDownloadResult>? request = null;
        Exception? primaryFailure = null;
        try
        {
            var workflow = new PlaylistUrlAcquisitionWorkflow(
                new RootedGateway(new AppPlaylistUrlDownloadGateway(new AppHttpClient(handler, 30000, clock)), root), _ => { }, clock);
            Uri pageUri = new("https://www.mediafire.com/file/test-page/playlist");
            Uri fileUri = new("https://download.mediafire.com/test-download/file.zip");
            Uri plainUri = new("https://example.invalid/file.zip");
            StagedResponse held;
            Uri start;
            if (sharedPageSecondRequest)
            {
                string html = "<html><body><a id=\"downloadButton\" href=\"" + fileUri + "\">Download</a></body></html>";
                StagedResponse page = handler.Stage(pageUri, Encoding.UTF8.GetBytes(html), holdBody: false, contentType: "text/html");
                page.ReleaseHeaders.TrySetResult();
                held = handler.Stage(fileUri);
                start = pageUri;
            }
            else
            {
                held = handler.Stage(plainUri);
                start = plainUri;
            }
            request = workflow.DownloadCandidateAsync(start, cancellationToken: operation.Token,
                options: new PlaylistUrlAcquisitionOptionsSnapshot { IdleTimeout = TimeSpan.FromSeconds(idleSeconds) });
            await AwaitPhaseOrRequestAsync(held.RequestStarted.Task, request, operation.Token);

            clock.Advance(TimeSpan.FromSeconds(idleSeconds) - TimeSpan.FromMilliseconds(1));
            Assert.IsFalse(held.RequestCancellation.IsCancellationRequested, "設定秒数の直前までは送信を取り消しません。");
            Assert.IsFalse(request.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            PlaylistUrlDownloadResult result = await request.WaitAsync(operation.Token);
            Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind, "時間切れは通常の取得失敗で、利用者取消ではありません。");
            Assert.IsTrue(held.RequestCancellation.IsCancellationRequested, "時間切れで実際の送信を取り消します。");
            Assert.IsFalse(operation.IsCancellationRequested);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            operation.Cancel();
            handler.ReleaseAll();
            Task[] owned = request == null ? [] : [request];
            try { await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, owned); }
            finally
            {
                if (owned.All(task => task.IsCompleted)) { handler.Dispose(); Directory.Delete(root, true); }
            }
        }
    }

    /// <summary>ヘッダー到着後、ヘッダー待ちの秒数を超えて定期的に本文が届けば取得でき、その後の停止だけが無受信期限で切れます。</summary>
    /// <remarks>
    /// ヘッダーを設定秒数の1ms前まで待たせた後、本文の各readを同じ秒数の1ms前で進めます。ヘッダー待ちの期限を本文へ持ち越す誤りと、
    /// 無受信期限を総転送時間にする誤りを検出します。5秒と600秒で、固定の30秒との違いを確認します。
    /// </remarks>
    [TestMethod]
    [DataRow(5, false)]
    [DataRow(600, false)]
    [DataRow(5, true)]
    [DataRow(600, true)]
    public async Task Body_PeriodicDataBeyondHeaderWaitDownloads_ThenStallExpiresByIdleTimeout(int idleSeconds, bool stall)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistBodyIdle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var clock = new ManualRequestTimeProvider();
        var handler = new StagedResponseHandler();
        using var body = new StagedBodyStream();
        Task<PlaylistUrlDownloadResult>? request = null;
        Exception? primaryFailure = null;
        TimeSpan nearlyIdle = TimeSpan.FromSeconds(idleSeconds) - TimeSpan.FromMilliseconds(1);
        try
        {
            var workflow = new PlaylistUrlAcquisitionWorkflow(
                new RootedGateway(new AppPlaylistUrlDownloadGateway(new AppHttpClient(handler, 30000, clock)), root), _ => { }, clock);
            Uri uri = new("https://example.invalid/file.zip");
            StagedResponse stage = handler.Stage(uri, body);
            request = workflow.DownloadCandidateAsync(uri, cancellationToken: operation.Token,
                options: new PlaylistUrlAcquisitionOptionsSnapshot { IdleTimeout = TimeSpan.FromSeconds(idleSeconds) });
            await AwaitPhaseOrRequestAsync(stage.RequestStarted.Task, request, operation.Token);
            clock.Advance(nearlyIdle);
            stage.ReleaseHeaders.TrySetResult();
            await AwaitPhaseOrRequestAsync(body.Started[0].Task, request, operation.Token);
            clock.Advance(nearlyIdle);
            body.Release[0].TrySetResult(1);
            await AwaitPhaseOrRequestAsync(body.Started[1].Task, request, operation.Token);
            clock.Advance(nearlyIdle);
            Assert.IsFalse(request.IsCompleted, "ヘッダー待ちを含む総経過が設定秒数を超えても、受信が続く間は継続します。");
            body.Release[1].TrySetResult(1);
            await AwaitPhaseOrRequestAsync(body.Started[2].Task, request, operation.Token);
            if (stall)
            {
                clock.Advance(TimeSpan.FromSeconds(idleSeconds));
                await body.Cancelled.Task.WaitAsync(operation.Token);
                Assert.IsFalse(request.IsCompleted, "取消しの観測後も、実readの終端まで応答と一時入力を保持します。");
                body.Terminal.TrySetResult();
                Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, (await request.WaitAsync(operation.Token)).Kind);
                Assert.AreEqual(0, Directory.GetFiles(root).Length);
            }
            else
            {
                body.Release[2].TrySetResult(0);
                PlaylistUrlDownloadResult result = await request.WaitAsync(operation.Token);
                Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
                CollectionAssert.AreEqual(new byte[] { 32, 32 }, File.ReadAllBytes(result.FilePath));
            }
            Assert.IsFalse(operation.IsCancellationRequested);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            operation.Cancel();
            handler.ReleaseAll();
            body.Terminal.TrySetResult();
            foreach (TaskCompletionSource<int> release in body.Release) { release.TrySetResult(0); }
            Task[] owned = request == null ? [] : [request];
            try { await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, owned); }
            finally
            {
                if (owned.All(task => task.IsCompleted)) { handler.Dispose(); Directory.Delete(root, true); }
            }
        }
    }

    // 段階の到達は合図で待ち、要求側の失敗が先に起きた場合はその場で検出します。
    private static async Task AwaitPhaseOrRequestAsync(Task phase, Task request, CancellationToken operationToken)
    {
        await Task.WhenAny(phase, request).WaitAsync(operationToken);
        if (request.IsCompleted) { await request; }
        Assert.IsTrue(phase.IsCompleted, "取得が目的の段階より先に終了しました。");
    }

    /// <summary>通信は本番のgatewayへ委ね、一時入力の置き場だけをケースの専有領域へ向けます。</summary>
    private sealed class RootedGateway(IPlaylistUrlDownloadGateway inner, string root) : IPlaylistUrlDownloadGateway
    {
        public Task<AppHttpResponse> OpenReadAsync(Uri uri, TimeSpan headerTimeout, CancellationToken cancellationToken)
            => inner.OpenReadAsync(uri, headerTimeout, cancellationToken);
        public string GetTemporaryDirectory() => root;
        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share) => new(path, mode, access, share);
        public bool FileExists(string path) => File.Exists(path);
        public void DeleteFile(string path) => File.Delete(path);
    }

}
