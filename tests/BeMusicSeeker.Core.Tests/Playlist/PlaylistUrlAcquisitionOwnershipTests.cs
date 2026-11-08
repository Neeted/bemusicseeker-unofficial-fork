using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>URLの取得判断と実workspaceから導入・表示への配送、受理されなかった入力の回収を確認します。</summary>
/// <remarks>取得は応答portで固定し、転送とpath caseの代表では実HTTPも使います。実stagingを使い、Windowとcompiled購読の接続はMainWindowの代表へ分担します。所有移転の代表では実ownerを、保留の代表では実推定・SQLite・FSまで通します。</remarks>
[TestClass]
public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    private readonly List<PlaylistWorkspaceTestPorts.OwnedPlaylistStore> ownedPlaylistStores = [];
    // インスタンスはcaseごとに分かれ、未回収TaskとDBを次caseの所有へ移しません。
    private readonly List<Task> ownedHttpTasks = [];

    /// <summary>methodのcleanupで残った元HTTP処理がすべて終端した場合だけDBを回収します。外側で期限を更新して待ち直しません。</summary>
    [TestCleanup]
    public void CleanupOwnedPlaylistStores()
    {
        Assert.IsFalse(ownedHttpTasks.Any(task => !task.IsCompleted),
            "HTTP所有処理が未回収のため、依存するplaylist DBを保持します。");
        var failures = new List<Exception>();
        for (int index = ownedPlaylistStores.Count - 1; index >= 0; index--)
        {
            try { ownedPlaylistStores[index].Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0) { throw new AggregateException(failures); }
        ownedPlaylistStores.Clear();
        ownedHttpTasks.Clear();
    }

    /// <summary>method cleanupが元Taskを回収できない場合、TestCleanupも非成功で実DBを保持し、gate解放後の処理が同じDBを使用できます。</summary>
    [TestMethod]
    public async Task HttpCleanup_UnfinishedProducerRetainsOwnedStoreThroughTestCleanup()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        using var expiredCleanup = new CancellationTokenSource();
        expiredCleanup.Cancel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? producer = null;
        Exception? primaryFailure = null;
        try
        {
            PlaylistWorkspaceTestPorts.OwnedPlaylistStore store = PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore(databaseScope, modelUiScheduler: new TestUiScheduler(() => null));
            ownedPlaylistStores.Add(store);
            producer = UseStoreAfterReleaseAsync(store.SongDbPath);
            ownedHttpTasks.Add(producer);
            await Assert.ThrowsExactlyAsync<AggregateException>(
                () => SingleRequestHttpServer.CompleteCleanupAsync(expiredCleanup.Token, null, ownedHttpTasks)).WaitAsync(operation.Token);
            Assert.ThrowsExactly<AssertFailedException>(CleanupOwnedPlaylistStores);
            Assert.IsFalse(producer.IsCompleted);
            Assert.IsTrue(File.Exists(store.SongDbPath), "外側cleanupも未終端producerのDBを削除しません。");
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            operation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, ownedHttpTasks);
            CleanupOwnedPlaylistStores();
        }

        async Task UseStoreAfterReleaseAsync(string songDbPath)
        {
            await release.Task;
            Assert.IsTrue(File.Exists(songDbPath));
            using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT 1"));
        }
    }

    /// <summary>対応する各譜面・archive名の応答を実stagingへ保存し、元basename・拡張子・bytesを保持します。取得物を別形式や別名へ変えて導入先へ渡す誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_SupportedChartAndArchiveNamesWriteExactBasenameExtensionAndBytes()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] expectedBytes = [0x10, 0x20, 0x30, 0x40];
        // 本番の対応譜面種別とarchive種別それぞれの保存契約。
        string[] supportedNames = ["chart.bms", "chart.bme", "chart.bml", "chart.pms", "chart.bmson", "package.zip", "package.7z", "package.rar", "package.lzh"];
        try
        {
            var gateway = new FakePlaylistUrlDownloadGateway(temporaryDirectory, expectedBytes);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            foreach (string fileName in supportedNames)
            {
                Uri uri = new("https://example.invalid/download/" + fileName);
                PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(uri);

                Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
                Assert.AreEqual(fileName, Path.GetFileName(result.FilePath));
                Assert.AreEqual(Path.GetExtension(fileName), Path.GetExtension(result.FilePath));
                CollectionAssert.AreEqual(expectedBytes, File.ReadAllBytes(result.FilePath));
            }

            Assert.AreEqual(supportedNames.Length, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>file、FTP、独自schemeと共有サイト風の独自schemeを入口で拒否し、通信・一時先取得・書込みを行いません。site名でscheme拒否を迂回する誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_UnsupportedInitialSchemeFailsBeforeGatewayOrTemp()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string[] unsupportedInitialUris =
        [
            "file:///C:/secret.zip",
            "ftp://example.invalid/package.zip",
            "custom+scheme://example.invalid/package.zip",
            "custom+scheme://drive.google.com/file/d/id/package.zip"
        ];
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            foreach (string uriText in unsupportedInitialUris)
            {
                PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(new Uri(uriText));

                Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind, uriText);
                Assert.IsTrue(result.IsUnsupportedScheme, uriText);
            }

            Assert.AreEqual(0, gateway.RequestedUris.Count);
            Assert.AreEqual(0, gateway.GetTemporaryDirectoryCount);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>Content-Disposition、対応する最終URI名、Location名、元名の優先とfallbackを実保存で確認します。queryへの依存、無効名の即採用、復号の退行をbasename・拡張子・完全bytesと一回の取得・書込みで検出します。</summary>
    [TestMethod]
    [DataRow("/download?id=42", "/song.zip", "chart.bmson", "/other.7z", "chart.bmson")]
    [DataRow("/download/123", "/song.zip", null, null, "song.zip")]
    [DataRow("/download/123?id=42", "/song.zip", null, null, "song.zip")]
    [DataRow("/original.zip", "/song%20release.7z", null, null, "song release.7z")]
    [DataRow("/original.zip", "/landing", null, null, "original.zip")]
    [DataRow("/original.zip?id=42", "/landing", null, null, "original.zip")]
    [DataRow("/original.zip", "/song.zip", null, "/other.7z", "song.zip")]
    [DataRow("/original.zip", "/landing", null, "/other.7z", "other.7z")]
    [DataRow("/original.zip", "/landing", null, "/other.txt", "original.zip")]
    public async Task DownloadCandidate_ChoosesResponseFileNameAndPreservesExactBytes(
        string requestedPath, string responsePath, string? contentDispositionName, string? locationPath, string expectedFileName)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] expectedBytes = [0x51, 0x52, 0x53];
        Uri uri = new("https://example.invalid" + requestedPath);
        try
        {
            using AppHttpResponse response = CreateHttpResponse(
                uri,
                expectedBytes,
                "application/octet-stream",
                contentDispositionName == null ? null : "attachment; filename=" + contentDispositionName,
                responseUri: new Uri("https://example.invalid" + responsePath),
                location: locationPath == null ? null : new Uri("https://example.invalid" + locationPath));
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            gateway.AddResponse(uri, () => response);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(uri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            Assert.AreEqual(expectedFileName, Path.GetFileName(result.FilePath));
            Assert.AreEqual(Path.GetExtension(expectedFileName), Path.GetExtension(result.FilePath));
            CollectionAssert.AreEqual(expectedBytes, File.ReadAllBytes(result.FilePath));
            CollectionAssert.AreEqual(new[] { uri }, gateway.RequestedUris);
            Assert.AreEqual(1, gateway.OpenWriteCount);
            Assert.IsFalse(response.ResponseStream.CanRead, "成功を返す前に応答本文を解放します。");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>共有HTMLのリンクを譜面・archive双方の直接URIへ解決し、ページ→実体の順に一回ずつ取得してbytesを保存します。HTML本文の誤保存と再帰取得の誤配送を検出します。</summary>
    [TestMethod]
    // 譜面本体とarchiveの双方を、同じ共有HTML解決経路で確認する。
    [DataRow("chart.bms")]
    [DataRow("package.zip")]
    public async Task DownloadCandidate_ResolvesKnownSharedHtmlPageToDirectSupportedFile(string fileName)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] expectedBytes = [0x61, 0x62, 0x63];
        Uri pageUri = new("https://www.mediafire.com/file/test-page/playlist");
        Uri directUri = new("https://download.mediafire.com/test-download/" + fileName);
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            string html = "<html><body><a id=\"downloadButton\" href=\"" + directUri + "\">Download</a></body></html>";
            gateway.AddResponse(pageUri, () => CreateHttpResponse(
                pageUri,
                Encoding.UTF8.GetBytes(html),
                "text/html"));
            gateway.AddResponse(directUri, () => CreateHttpResponse(
                directUri,
                expectedBytes,
                "application/octet-stream"));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(pageUri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            Assert.AreEqual(fileName, Path.GetFileName(result.FilePath));
            CollectionAssert.AreEqual(expectedBytes, File.ReadAllBytes(result.FilePath));
            CollectionAssert.AreEqual(
                new[] { pageUri.AbsoluteUri, directUri.AbsoluteUri },
                gateway.RequestedUris.Select(requestedUri => requestedUri.AbsoluteUri).ToArray());
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>HTTP要求でも応答URIがfileなら不対応失敗として返し、本文を書込みません。redirect後のscheme検査漏れを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_UnsupportedResponseSchemeFailsBeforeWriting()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Uri requestUri = new("https://example.invalid/download/package.zip");
        Uri responseUri = new("file:///C:/secret.zip");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            gateway.AddResponse(requestUri, () => new AppHttpResponse(
                responseUri,
                new MemoryStream([0x41, 0x42, 0x43], writable: false)));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(requestUri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind);
            Assert.IsTrue(result.IsUnsupportedScheme);
            CollectionAssert.AreEqual(new[] { requestUri.AbsoluteUri }, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            Assert.AreEqual(1, gateway.GetTemporaryDirectoryCount);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>HTTP応答が不対応schemeの場合、workspaceは導入もブラウザも起動しません。取得失敗の分類を捨てて危険な代替処理へ進む誤りを検出します。</summary>
    [TestMethod]
    public async Task WorkspaceDoesNotBrowserFallbackAfterUnsupportedResponseScheme()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Uri requestUri = new("https://example.invalid/download/package.zip");
        Uri responseUri = new("file:///C:/secret.zip");
        int installCount = 0;
        int browserOpenCount = 0;
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            gateway.AddResponse(requestUri, () => new AppHttpResponse(
                responseUri,
                new MemoryStream([0x41, 0x42, 0x43], writable: false)));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot
                {
                    ScanBmsFilesOnStartup = true,
                    AutoInstall = true
                },
                acquisitionWorkflow: workflow,
                installSink: _ => { installCount++; return true; },
                browserSink: _ => browserOpenCount++);

            await workspace.RunSinglePlaylistUrlAsync(requestUri);

            Assert.AreEqual(0, installCount);
            Assert.AreEqual(1, gateway.RequestedUris.Count);
            Assert.AreEqual(0, browserOpenCount);
            Assert.AreEqual(1, gateway.GetTemporaryDirectoryCount);
            Assert.AreEqual(0, gateway.OpenWriteCount);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>共有HTMLがfileリンクを返しても二段目を取得せず、書込みなしの不対応失敗を返します。解決したリンクのscheme検査漏れを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_UnsupportedRecursiveHtmlSchemeFailsWithoutSecondGatewayHop()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Uri pageUri = new("https://www.mediafire.com/file/test-page/playlist");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            string html = "<html><body><a id=\"downloadButton\" href=\"file:///C:/secret.zip\">Download</a></body></html>";
            gateway.AddResponse(pageUri, () => CreateHttpResponse(
                pageUri,
                Encoding.UTF8.GetBytes(html),
                "text/html"));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(pageUri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind);
            Assert.IsTrue(result.IsUnsupportedScheme);
            CollectionAssert.AreEqual(new[] { pageUri.AbsoluteUri }, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            Assert.AreEqual(1, gateway.GetTemporaryDirectoryCount);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>Google確認form、イベント表、会場・譜面ページのJSONからfile URIを抽出しても追加通信・保存へ進みません。抽出形式ごとの再帰入口で拒否が抜ける誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_UnsupportedRecursiveSchemeFailsForKnownHtmlAndJsonSourceForms()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var cases = new (Uri PageUri, string Html)[]
        {
            (
                new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"),
                "<form id=\"download-form\" method=\"get\" action=\"file:///C:/secret.zip\"><input type=\"hidden\" name=\"id\" value=\"abc123\"></form>"),
            (
                new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"),
                "<Th>DownLoadAddress</Th><td><a href=\"file:///C:/secret.zip\">Download</a></td>"),
            (
                new Uri("https://venue.bmssearch.net/freebattle/30"),
                "<script>self.__next_f.push([1,\"2c:{\\\"downloadURL\\\":\\\"file:///C:/secret.zip\\\",\\\"type\\\":\\\"CORE\\\"}\"])</script>"),
            (
                new Uri("https://bmssearch.net/bmses/2uLp8a8bJYLmrx"),
                "<script>self.__next_f.push([1,\"7:{\\\"downloads\\\":[{\\\"url\\\":\\\"file:///C:/secret.zip\\\"}]}\"])</script>")
        };
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            foreach ((Uri pageUri, string html) in cases)
            {
                gateway.AddResponse(pageUri, () => CreateHttpResponse(
                    pageUri,
                    Encoding.UTF8.GetBytes(html),
                    "text/html"));
            }
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            foreach ((Uri pageUri, string _) in cases)
            {
                PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(pageUri);

                Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind, pageUri.ToString());
                Assert.IsTrue(result.IsUnsupportedScheme, pageUri.ToString());
            }

            CollectionAssert.AreEqual(
                cases.Select(testCase => testCase.PageUri.AbsoluteUri).ToArray(),
                gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>不対応拡張子と譜面名を持つHTML応答はブラウザ対象へ分類し、stagingへ保存しません。ファイル名だけでHTMLを譜面扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_UnsupportedNamesAndHtmlChartReturnBrowserFallbackWithoutWriting()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] responseBytes = [0x71, 0x72, 0x73];
        Uri unsupportedUri = new("https://example.invalid/download/package.txt");
        Uri htmlChartUri = new("https://example.invalid/download/chart.bms");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            gateway.AddResponse(unsupportedUri, () => new AppHttpResponse(
                unsupportedUri,
                new MemoryStream(responseBytes, writable: false)));
            gateway.AddResponse(htmlChartUri, () => CreateHttpResponse(
                htmlChartUri,
                responseBytes,
                "text/html"));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult unsupportedResult = await workflow.DownloadCandidateAsync(unsupportedUri);
            PlaylistUrlDownloadResult htmlChartResult = await workflow.DownloadCandidateAsync(htmlChartUri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.BrowserFallback, unsupportedResult.Kind);
            Assert.AreEqual(PlaylistUrlDownloadResultKind.BrowserFallback, htmlChartResult.Kind);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>通信例外を取得失敗として返し、実ファイルを書込みません。失敗した応答を保存済み扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_TransportExceptionReturnsFailedWithoutWriting()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Uri uri = new("https://example.invalid/download/package.zip");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory)
            {
                TransportException = new HttpRequestException("transport failure")
            };
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(uri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>通信待機中の明示取消はOperationCanceledExceptionで終端し、取得失敗分類やファイルへ置き換えません。取消を隠して処理を続ける誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_ExplicitCancellationPropagatesOperationCanceledException()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
        Task? acquisition = null;
        try
        {
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            using var cancellation = new CancellationTokenSource();
            acquisition = workflow.DownloadCandidateAsync(
                new Uri("https://example.invalid/download/running.zip"),
                cancellationToken: cancellation.Token);

            await TestUiDispatcherHost.AwaitNotificationAsync(gateway.ReadStarted.Task, acquisition, "url-acquisition.read-started");
            cancellation.Cancel();
            gateway.Response.TrySetCanceled(cancellation.Token);

            try
            {
                await acquisition;
                Assert.Fail("Explicit cancellation should propagate as an OperationCanceledException.");
            }
            catch (OperationCanceledException)
            {
            }
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);
        }
        finally
        {
            gateway.Response.TrySetCanceled();
            try
            {
                if (acquisition != null)
                {
                    try { await acquisition; } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }
    }

    /// <summary>本体・差分の単体右クリックは自動導入設定によらず元の共有URLをUI配送で一度開きます。取得失敗後の代替でも同じ見た目になる誤接続を、通信・保存・導入・確認・進捗・展開がないことで検出します。</summary>
    [TestMethod]
    [DataRow(false, false, "https://drive.google.com/file/d/main-id/view", "https://drive.google.com/file/d/main-id/view")]
    [DataRow(false, true, "https://drive.google.com/file/d/main-id/view", "https://drive.google.com/file/d/main-id/view")]
    [DataRow(true, false, "https://www.dropbox.com/scl/fi/diff-id/diff.zip?dl=0", "https://www.dropbox.com/scl/fi/diff-id/diff.zip?dl=0")]
    [DataRow(true, true, "https://www.dropbox.com/scl/fi/diff-id/diff.zip?dl=0", "https://www.dropbox.com/scl/fi/diff-id/diff.zip?dl=0")]
    public async Task ContextMenuSingleUrl_OpensOriginalUrlWithoutAcquisition(bool isDiff, bool autoInstall, string sourceUrl, string expectedBrowserUrl)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var gateway = new RecordingPlaylistUrlDownloadGateway(Path.Combine(migrationWorkspace.RootDirectory, Guid.NewGuid().ToString("N")));
        var dialogs = new RecordingPlaylistUrlDialogService();
        var opened = new List<Uri>();
        int installCount = 0, progressCount = 0, expansionCount = 0, dispatchCount = 0;
        bool insideScheduler = false;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = autoInstall, ScanBmsFilesOnStartup = true },
            acquisitionWorkflow: new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
            installSink: _ => { installCount++; return true; },
            browserSink: uri => { Assert.IsTrue(insideScheduler); opened.Add(uri); },
            treeExpansionSink: () => expansionCount++, dialogService: dialogs,
            presentationScheduler: action =>
            {
                dispatchCount++;
                insideScheduler = true;
                try { action(); }
                finally { insideScheduler = false; }
                return Task.CompletedTask;
            });
        workspace.PlaylistUrlDownloadStatusChanged += (_, _) => progressCount++;
        PlaylistDetailRow row = CreatePlaylistUrlRow(isDiff ? "https://example.invalid/unused-main.zip" : sourceUrl,
            isDiff ? sourceUrl : "https://example.invalid/unused-diff.zip");

        await workspace.RunPlaylistUrlActionAsync([row], isDiff);

        CollectionAssert.AreEqual(new[] { expectedBrowserUrl }, opened.Select(uri => uri.AbsoluteUri).ToArray());
        Assert.AreEqual(1, dispatchCount);
        Assert.AreEqual(0, gateway.RequestedUris.Count);
        Assert.AreEqual(0, gateway.GetTemporaryDirectoryCount);
        Assert.AreEqual(0, gateway.OpenWriteCount);
        Assert.AreEqual(0, installCount);
        Assert.AreEqual(0, dialogs.Confirmations.Count);
        Assert.AreEqual(0, dialogs.Messages.Count);
        Assert.AreEqual(0, progressCount);
        Assert.AreEqual(0, expansionCount);
    }

    /// <summary>空行集合と空・相対・非HTTP(S)の指定列は右クリックで無作用です。空入力の誤配送とブラウザー経路のscheme検査漏れを検出します。</summary>
    [TestMethod]
    public async Task ContextMenuSingleUrl_InvalidOrEmptyInputHasNoEffects()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var gateway = new RecordingPlaylistUrlDownloadGateway(Path.Combine(migrationWorkspace.RootDirectory, Guid.NewGuid().ToString("N")));
        var dialogs = new RecordingPlaylistUrlDialogService();
        int presentationCount = 0, installCount = 0, progressCount = 0, expansionCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
            acquisitionWorkflow: new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
            installSink: _ => { installCount++; return true; },
            treeExpansionSink: () => expansionCount++, dialogService: dialogs,
            presentationScheduler: action => { presentationCount++; action(); return Task.CompletedTask; });
        workspace.PlaylistUrlDownloadStatusChanged += (_, _) => progressCount++;
        await workspace.RunPlaylistUrlActionAsync([], false);
        foreach (string? text in new[] { null, "", "relative/package.zip", "file:///C:/secret.zip", "ftp://example.invalid/package.zip" })
        {
            await workspace.RunPlaylistUrlActionAsync([CreatePlaylistUrlRow(text, text)], false);
            await workspace.RunPlaylistUrlActionAsync([CreatePlaylistUrlRow(text, text)], true);
        }
        Assert.AreEqual(0, presentationCount);
        Assert.AreEqual(0, gateway.RequestedUris.Count);
        Assert.AreEqual(0, gateway.GetTemporaryDirectoryCount);
        Assert.AreEqual(0, gateway.OpenWriteCount);
        Assert.AreEqual(0, installCount);
        Assert.AreEqual(0, progressCount);
        Assert.AreEqual(0, expansionCount);
        Assert.AreEqual(0, dialogs.Confirmations.Count);
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    /// <summary>右クリック要求はUI配送の実終端を待ちます。配送を開始しただけで成功として返す誤りを、解放前のTask未完了で検出します。</summary>
    [TestMethod]
    public async Task ContextMenuSingleUrl_AwaitsBrowserPresentation()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int browserCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(), browserSink: _ => browserCount++,
            presentationScheduler: async action => { entered.SetResult(); await released.Task; action(); });
        Task request = workspace.RunPlaylistUrlActionAsync([CreatePlaylistUrlRow("https://example.invalid/package.zip", null)], false);
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, request, "context-menu.browser-presentation");
            Assert.IsFalse(request.IsCompleted);
            Assert.AreEqual(0, browserCount);
        }
        finally { released.TrySetResult(); await request; }
        Assert.AreEqual(1, browserCount);
    }

    /// <summary>同じURLを持つ別二行でも複数行操作として確認し、回答前は未取得、承認後は重複を除いた一取得物を一度導入へ渡します。URL件数で単体ブラウザーへ戻す誤りを検出します。</summary>
    [TestMethod]
    public async Task ContextMenuDuplicateRows_ConfirmsAndImportsOnceWithoutBrowser()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? request = null;
        try
        {
            Uri uri = new("https://example.invalid/package.zip");
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(uri, () => new AppHttpResponse(uri, new MemoryStream([1, 2, 3], writable: false)));
            var dialogs = new RecordingPlaylistUrlDialogService { PendingConfirmation = confirmation };
            int installCount = 0, browserCount = 0;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
                acquisitionWorkflow: new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                installSink: paths => { installCount++; Assert.AreEqual(1, paths.Count); Assert.IsTrue(File.Exists(paths[0])); return true; },
                browserSink: _ => browserCount++, dialogService: dialogs);
            request = workspace.RunPlaylistUrlActionAsync([
                CreatePlaylistUrlRow(uri.AbsoluteUri, null), CreatePlaylistUrlRow(uri.AbsoluteUri, null)], false);
            Assert.AreEqual(1, dialogs.Confirmations.Count);
            Assert.IsFalse(request.IsCompleted);
            Assert.AreEqual(0, gateway.RequestedUris.Count);
            Assert.AreEqual(0, installCount);
            confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await request;
            Assert.AreEqual(1, gateway.RequestedUris.Count);
            Assert.AreEqual(1, gateway.OpenWriteCount);
            Assert.AreEqual(1, installCount);
            Assert.AreEqual(0, browserCount);
        }
        finally
        {
            confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            try { if (request != null) { await request; } }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>セルクリックの宣言サイズ超過は保存・導入・ブラウザー代替を行いません。共有したブラウザー配送へサイズ拒否を誤って流す退行を小さい応答で検出します。</summary>
    [TestMethod]
    public async Task WorkspaceSizeLimitDoesNotBrowserFallback()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Uri uri = new("https://example.invalid/package.zip");
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(uri, () =>
            {
                var message = new HttpResponseMessage { Content = new ByteArrayContent([]) };
                message.Content.Headers.ContentLength = 512L * 1024 * 1024 + 1;
                return new AppHttpResponse(uri, message, new MemoryStream());
            });
            int browserCount = 0, installCount = 0;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { AutoInstall = true, ScanBmsFilesOnStartup = true },
                acquisitionWorkflow: new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                installSink: _ => { installCount++; return true; }, browserSink: _ => browserCount++);
            await workspace.RunSinglePlaylistUrlAsync(uri);
            Assert.AreEqual(1, gateway.RequestedUris.Count);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, installCount);
            Assert.AreEqual(0, browserCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>起動走査を切った単独URLは表示dispatchを通してブラウザへ一回配送します。自動導入条件を無視する誤りとUI配送の接続漏れを検出します。</summary>
    [TestMethod]
    public async Task WorkspaceForwardsBrowserFallbackThroughPresentationDispatch()
    {
        int dispatchCount = 0;
        Uri? openedUri = null;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action =>
        {
            dispatchCount++;
            action();
        }, () => new PlaylistUrlAcquisitionOptionsSnapshot
        {
            ScanBmsFilesOnStartup = false,
            AutoInstall = true
        }, browserSink: uri => openedUri = uri);

        await workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/"));

        Assert.AreEqual("https://example.invalid/folder/", openedUri?.ToString());
        Assert.AreEqual(1, dispatchCount);
    }

    /// <summary>自動導入なしでもfile・共有サイト風の独自schemeを通信、導入、ブラウザへ配送しません。ブラウザ経路で入口拒否を迂回する誤りを検出します。</summary>
    [TestMethod]
    [DataRow("file:///C:/secret.zip")]
    [DataRow("custom+scheme://drive.google.com/file/d/id/package.zip")]
    public async Task WorkspaceDoesNotOpenOrInstallUnsupportedInitialSchemeWhenAutoInstallIsDisabled(string uriText)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        int installCount = 0;
        int browserOpenCount = 0;
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot
                {
                    ScanBmsFilesOnStartup = false,
                    AutoInstall = false
                },
                acquisitionWorkflow: workflow,
                installSink: _ => { installCount++; return true; },
                browserSink: _ => browserOpenCount++);

            await workspace.RunSinglePlaylistUrlAsync(new Uri(uriText));

            Assert.AreEqual(0, installCount);
            Assert.AreEqual(0, browserOpenCount);
            Assert.AreEqual(0, gateway.RequestedUris.Count);
            Assert.AreEqual(0, gateway.GetTemporaryDirectoryCount);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>通常HTTPの通信失敗はworkspaceからブラウザへ配送します。不対応schemeの拒否をすべての失敗へ広げる誤りを検出します。</summary>
    [TestMethod]
    public async Task WorkspaceBrowserFallbackRemainsAvailableAfterOrdinaryHttpAcquisitionFailure()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Uri requestUri = new("https://example.invalid/download/package.zip");
        int browserOpenCount = 0;
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory)
            {
                TransportException = new HttpRequestException("transport failure")
            };
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot
                {
                    ScanBmsFilesOnStartup = true,
                    AutoInstall = true
                },
                acquisitionWorkflow: workflow,
                browserSink: _ => browserOpenCount++);

            await workspace.RunSinglePlaylistUrlAsync(requestUri);

            Assert.AreEqual(1, gateway.RequestedUris.Count);
            Assert.AreEqual(1, browserOpenCount);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>通信の内部故障はブラウザー代替に変換せず、所有する進捗と受付を終えます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkspaceInternalAcquisitionFailureDoesNotOpenBrowser(bool resourceFailure)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Exception failure = resourceFailure ? new OutOfMemoryException("resource failure") : new InvalidOperationException("internal failure");
        var gateway = new RecordingPlaylistUrlDownloadGateway(migrationWorkspace.RootDirectory) { TransportException = failure };
        int browserOpenCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
            acquisitionWorkflow: new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
            browserSink: _ => browserOpenCount++);
        Exception? observed = null;
        try { await workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/package.zip")); }
        catch (Exception exception) { observed = exception; }
        Assert.AreSame(failure, observed);
        Assert.AreEqual(0, browserOpenCount);
        Assert.AreEqual(0, gateway.OpenWriteCount);
        Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
    }

    /// <summary>読取り・応答破棄の失敗で未引渡しファイルを回収し、回収不能だけは通常失敗へ変換しません。</summary>
    [TestMethod]
    [DataRow("read", false)]
    [DataRow("read", true)]
    [DataRow("dispose", false)]
    [DataRow("dispose", true)]
    [DataRow("read-and-dispose", false)]
    [DataRow("read-and-dispose", true)]
    public async Task DownloadFailureReclaimsUnpublishedInputAndPreservesUnresolvedCleanup(string stage, bool failCleanup)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var failure = new IOException("response " + stage + " failed");
            var disposeFailure = new IOException("response disposal also failed");
            var stream = new FailingDownloadStream(failure, failOnDispose: stage == "dispose",
                additionalDisposeFailure: stage == "read-and-dispose" ? disposeFailure : null);
            var gateway = new RecordingPlaylistUrlDownloadGateway(root)
            {
                DeleteFailure = failCleanup ? new IOException("staging cleanup failed") : null
            };
            Uri uri = new("https://example.invalid/package.zip");
            gateway.AddResponse(uri, () => new AppHttpResponse(uri, stream));
            var logs = new List<string>();
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, logs.Add);
            var downloadedKeys = new HashSet<string>(StringComparer.Ordinal);
            PlaylistUrlDownloadResult? result = null;
            Exception? observed = null;
            try { result = await workflow.DownloadCandidateAsync(uri, downloadedKeys); }
            catch (Exception exception) { observed = exception; }

            Assert.AreEqual(0, downloadedKeys.Count, "引渡し前の回収対象を取得成功済みとして残しません。");
            Assert.IsTrue(stream.WasDisposed);
            Assert.AreEqual(1, gateway.OpenWriteCount);
            Assert.AreEqual(1, gateway.DeleteCount);
            Assert.AreEqual(failCleanup, File.Exists(Path.Combine(root, "package.zip")));
            if (failCleanup || stage == "read-and-dispose")
            {
                Assert.IsNull(result);
                Assert.IsNotNull(observed);
                StringAssert.Contains(observed.ToString(), failure.Message);
                if (failCleanup) { StringAssert.Contains(observed.ToString(), "staging cleanup failed"); }
                if (stage == "read-and-dispose") { StringAssert.Contains(observed.ToString(), disposeFailure.Message); }
            }
            else
            {
                Assert.IsNull(observed);
                Assert.IsNotNull(result);
                Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind);
                Assert.IsTrue(logs.Exists(message => message.Contains(failure.ToString(), StringComparison.Ordinal)));

                gateway.AddResponse(uri, () => new AppHttpResponse(uri, new MemoryStream(new byte[] { 4, 5, 6 })));
                PlaylistUrlDownloadResult retried = await workflow.DownloadCandidateAsync(uri, downloadedKeys);
                Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, retried.Kind);
                Assert.IsTrue(File.Exists(retried.FilePath));
                Assert.AreEqual(1, downloadedKeys.Count);
                await workflow.ReclaimStagedFilesAsync([retried.FilePath]);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>セルクリックと単体右クリックのブラウザsink例外を要求Taskへ返します。表示先の失敗を成功扱いする誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BrowserFallbackSinkExceptionIsNotSuppressed(bool contextMenu)
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            browserSink: _ => throw new InvalidOperationException("browser sink failure"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => contextMenu
            ? workspace.RunPlaylistUrlActionAsync([CreatePlaylistUrlRow("https://example.invalid/folder/", null)], false)
            : workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

    /// <summary>セルクリックと単体右クリックの表示scheduler失敗を要求Taskの終端まで伝播します。非同期配送を待たず成功を返す誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BrowserFallbackPresentationFailureIsPropagatedThroughAwaitableScheduler(bool contextMenu)
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            presentationScheduler: _ => Task.FromException(
                new InvalidOperationException("presentation dispatch failure")));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => contextMenu
            ? workspace.RunPlaylistUrlActionAsync([CreatePlaylistUrlRow("https://example.invalid/folder/", null)], false)
            : workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

    /// <summary>一括取得の確認後にブラウザ対象一件の結果通知を届け、statusを非稼働へ戻します。単独ブラウザdispatchへの混入と終端表示漏れを検出します。</summary>
    [TestMethod]
    public async Task BulkBrowserFallbackPublishesSummaryAndReturnsToInactiveState()
    {
        int dispatchCount = 0;
        var dialogs = new RecordingPlaylistUrlDialogService();
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action =>
        {
            dispatchCount++;
            action();
        }, () => new PlaylistUrlAcquisitionOptionsSnapshot(), dialogService: dialogs);
        List<PlaylistUrlDownloadStatusSnapshot> statuses = [];
        workspace.PlaylistUrlDownloadStatusChanged += (_, value) => statuses.Add(value);

        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/folder/")],
            isDiffUrl: false);

        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual(1, dialogs.Messages.Count);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "1");
        Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
        Assert.IsTrue(statuses.Count >= 2);
        Assert.IsFalse(statuses[statuses.Count - 1].IsActive);
        Assert.AreEqual(0, dispatchCount);
    }

    /// <summary>未回答の確認Taskを保持して要求が未完了で戻り、OK後に結果通知と非稼働終端へ進みます。確認を待たず受付・完了する誤りを検出します。</summary>
    [TestMethod]
    public async Task BulkPlaylistUrlConfirmationAwaitsDialogWithoutBlocking()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            PendingConfirmation = new TaskCompletionSource<UiDialogResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            () => new PlaylistUrlAcquisitionOptionsSnapshot(),
            dialogService: dialogs);

        Task acquisition = workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/pending-confirmation/")],
            isDiffUrl: false);

        try
        {
            Assert.IsFalse(acquisition.IsCompleted);
            Assert.AreEqual(1, dialogs.Confirmations.Count);

            dialogs.PendingConfirmation.SetResult(
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await acquisition;

            Assert.AreEqual(1, dialogs.Messages.Count);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
        }
        finally
        {
            dialogs.PendingConfirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            await acquisition;
        }
    }

    /// <summary>一括確認の未回答中でも単独の読取りURLをブラウザへ配送し、先の要求は保留します。未受理確認をdownload稼働扱いして読取りまで拒む誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlReadOnlyRouteRemainsAllowedDuringUnacceptedConfirmation()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            PendingConfirmation = new TaskCompletionSource<UiDialogResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        int browserOpenCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            browserSink: _ => browserOpenCount++,
            dialogService: dialogs);

        Task first = workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/first-pending/")],
            isDiffUrl: false);
        Assert.IsFalse(first.IsCompleted);

        try
        {
            await workspace.RunSinglePlaylistUrlAsync(
                new Uri("https://example.invalid/second-during-confirmation/"));
            Assert.AreEqual(1, dialogs.Confirmations.Count);
            Assert.AreEqual(1, browserOpenCount);
            Assert.IsFalse(first.IsCompleted);
        }
        finally
        {
            dialogs.PendingConfirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await first;
        }
    }

    /// <summary>取消と確認service失敗の後でも次の明示一括要求を確認へ届けます。未受理経路でcommand gateを残す誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlCommandGateReleasesAfterRejectionAndDialogFailure()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            dialogService: dialogs);

        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/rejected/")],
            isDiffUrl: false);
        Assert.AreEqual(1, dialogs.Confirmations.Count);

        dialogs.ConfirmationResult = UiDialogResult.Failed(
            new InvalidOperationException("dialog unavailable"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => workspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/failed-dialog/")],
                isDiffUrl: false));
        Assert.AreEqual(2, dialogs.Confirmations.Count);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/retry/")],
            isDiffUrl: false);
        Assert.AreEqual(3, dialogs.Confirmations.Count);
    }

    /// <summary>結果表示だけの失敗を操作失敗へ戻さず、次の明示要求で確認・結果表示を行います。通知による結果の上書きと受付解放漏れを検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlCommandGateReleasesAfterSummaryFailure()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            MessageResult = UiDialogResult.Failed(
                new InvalidOperationException("summary unavailable"))
        };
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            dialogService: dialogs);

        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/summary-failure/")],
            isDiffUrl: false);
        Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);

        dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/summary-retry/")],
            isDiffUrl: false);

        Assert.AreEqual(2, dialogs.Confirmations.Count);
        Assert.AreEqual(2, dialogs.Messages.Count);
    }

    /// <summary>閉じた確認のCancelは結果表示へ進まず、OKを伴う閉じ方は処理して結果表示します。ClosedByUserだけで利用者の選択を捨てる誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlConfirmationPreservesClosedDialogDecision()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.Cancel)
        };
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            dialogService: dialogs);

        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/closed-cancel/")],
            isDiffUrl: false);
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual(0, dialogs.Messages.Count);

        dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.OK);
        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/closed-ok/")],
            isDiffUrl: false);
        Assert.AreEqual(2, dialogs.Confirmations.Count);
        Assert.AreEqual(1, dialogs.Messages.Count);
    }

    /// <summary>単独・重複と欠損を含む複数選択・欠損だけの選択から本体/差分URLの可否を判断し、hashのない行は外部検索不可にします。行数とURL種別の条件混同を検出します。</summary>
    [TestMethod]
    public void PlaylistUrlContextMenuAvailability_PreservesSingleAndBulkUrlPolicy()
    {
        PlaylistDetailRow first = CreatePlaylistUrlRow(
            "https://example.invalid/package.zip",
            "https://example.invalid/diff.zip");
        PlaylistDetailRow duplicate = CreatePlaylistUrlRow(
            "https://example.invalid/package.zip",
            null);
        PlaylistDetailRow invalid = CreatePlaylistUrlRow(null, null);
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action());

        PlaylistUrlContextMenuAvailability single = workspace.CapturePlaylistUrlContextMenuAvailability(
            first,
            [first]);
        PlaylistUrlContextMenuAvailability bulk = workspace.CapturePlaylistUrlContextMenuAvailability(
            first,
            [first, duplicate, invalid]);
        PlaylistUrlContextMenuAvailability unavailable = workspace.CapturePlaylistUrlContextMenuAvailability(
            invalid,
            [invalid]);

        Assert.IsTrue(single.IsPlaylistContext);
        Assert.IsFalse(single.IsBulkContext);
        Assert.IsTrue(single.CanOpenUrl);
        Assert.IsTrue(single.CanOpenDiffUrl);
        Assert.IsFalse(single.CanFindExternalPackage);
        Assert.IsTrue(bulk.IsBulkContext);
        Assert.IsTrue(bulk.CanOpenUrl);
        Assert.IsTrue(bulk.CanOpenDiffUrl);
        Assert.IsFalse(unavailable.CanOpenUrl);
        Assert.IsFalse(unavailable.CanOpenDiffUrl);
    }

    /// <summary>確認未回答中でも有効な本体・差分URLを操作可能に保ち、hashなし外部検索は不可にします。未受理確認によってmenuを無効化する誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlContextMenuAvailability_DoesNotTreatPendingConfirmationAsDownload()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            PendingConfirmation = new TaskCompletionSource<UiDialogResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        PlaylistDetailRow row = CreatePlaylistUrlRow(
            "https://example.invalid/pending/",
            "https://example.invalid/pending-diff/");
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
            action => action(),
            dialogService: dialogs);

        Task acquisition = workspace.RunPlaylistUrlBatchAsync([new Uri("https://example.invalid/pending/")], isDiffUrl: false);
        try
        {
            Assert.IsFalse(acquisition.IsCompleted);

            PlaylistUrlContextMenuAvailability availability = workspace.CapturePlaylistUrlContextMenuAvailability(
                row,
                [row]);

            Assert.IsTrue(availability.CanOpenUrl);
            Assert.IsTrue(availability.CanOpenDiffUrl);
            Assert.IsFalse(availability.CanFindExternalPackage);

            dialogs.PendingConfirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            await acquisition;
        }
        finally
        {
            dialogs.PendingConfirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            await acquisition;
        }
    }

    /// <summary>実取得の通信待機中はURLメニューを不可にし、単体右クリックを投入してもブラウザーを開きません。稼働中の入口抑止漏れを検出し、MD5なし外部検索も不可に保ちます。</summary>
    [TestMethod]
    public async Task PlaylistUrlContextMenuAvailability_DisablesActionsWhileDownloading()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
        int browserCount = 0;
        Task? acquisition = null;
        try
        {
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistDetailRow row = CreatePlaylistUrlRow(
                "https://example.invalid/running.zip",
                "https://example.invalid/running-diff.zip");
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                acquisitionWorkflow: acquisitionWorkflow,
                browserSink: _ => browserCount++,
                dialogService: dialogs);

            acquisition = workspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/running.zip")],
                isDiffUrl: false);
            await TestUiDispatcherHost.AwaitNotificationAsync(gateway.ReadStarted.Task, acquisition, "url-acquisition.read-started");

            PlaylistUrlContextMenuAvailability availability = workspace.CapturePlaylistUrlContextMenuAvailability(
                row,
                [row]);

            Assert.IsFalse(availability.CanOpenUrl);
            Assert.IsFalse(availability.CanOpenDiffUrl);
            Assert.IsFalse(availability.CanFindExternalPackage);

            await workspace.RunPlaylistUrlActionAsync([row], false);
            await workspace.RunPlaylistUrlActionAsync([row], true);
            Assert.AreEqual(0, browserCount);
            Assert.AreEqual(1, dialogs.Confirmations.Count);

            gateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/running.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            await acquisition;
        }
        finally
        {
            gateway.Response.TrySetCanceled();
            try
            {
                if (acquisition != null)
                {
                    try { await acquisition; } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }
    }

    /// <summary>実取得（URL・外部パッケージ探索）からhubへ種別ごとの書式の進捗行を届け、取消受付後は再取消を不可にして書式を保ち、取得終端で行を除去します。進捗購読の接続漏れ・取消後の書式の取り違え・終端行の残留を検出します。</summary>
    [TestMethod]
    [DataRow(false)] // URL取得は URL 取得の書式を使います。
    [DataRow(true)] // 外部パッケージ探索は探索の書式を使います。
    public async Task PlaylistUrlProgressAttachedHubPublishesActiveCancelAndTerminalStates(bool lookup)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
        var lookupProvider = new BlockingLookupProvider();
        Task? acquisition = null;
        try
        {
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                acquisitionWorkflow: acquisitionWorkflow,
                dialogService: dialogs,
                externalLookupService: lookup ? new PlaylistExternalPackageLookupService([lookupProvider]) : null);
            var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
            hub.AttachPlaylistProgressSources(workspace, action => action(), () => false);
            string expectedLabel = string.Format(
                lookup ? Resources.Playlist_external_package_lookup_progress_label_format : Resources.Playlist_url_download_progress_label_format, 0, 1);

            acquisition = lookup
                ? workspace.RunPlaylistExternalPackageLookupAsync([CreatePlaylistExternalPackageRow("11111111111111111111111111111111")])
                : workspace.RunPlaylistUrlBatchAsync([new Uri("https://example.invalid/running.zip")], isDiffUrl: false);
            await TestUiDispatcherHost.AwaitNotificationAsync(lookup ? lookupProvider.Started.Task : gateway.ReadStarted.Task, acquisition, "url-acquisition.read-started");

            Assert.IsTrue(hub.Rows.Any(row => row.Key == "url"));
            Assert.IsTrue(hub.Rows.Single(row => row.Key == "url").CanCancel);
            Assert.AreEqual(expectedLabel, hub.Rows.Single(row => row.Key == "url").Label);
            workspace.CancelPlaylistUrlDownload();
            Assert.IsFalse(hub.Rows.Single(row => row.Key == "url").CanCancel);
            Assert.AreEqual(expectedLabel, hub.Rows.Single(row => row.Key == "url").Label, "取消後の再送も同じ種別の書式を保ちます。");

            gateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/running.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            lookupProvider.Result.TrySetResult(null);
            await acquisition;

            Assert.IsFalse(hub.Rows.Any(row => row.Key == "url"));
            Assert.IsFalse(hub.HasRows);
        }
        finally
        {
            gateway.Response.TrySetCanceled();
            lookupProvider.Result.TrySetCanceled();
            try
            {
                if (acquisition != null)
                {
                    try { await acquisition; } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }
    }

    /// <summary>探索の開始と応答を試験側で制御し、取消を観測するproviderです。</summary>
    private sealed class BlockingLookupProvider : IPlaylistExternalPackageLookupProvider
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<PlaylistExternalPackageLookupResult?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "blocking-test";

        public async Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            // 取消の継続を呼出し元スレッドへ戻さないよう非同期に再開させます。本ケースは取消時の同期継続という既存事象を対象にしません。
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => canceled.TrySetResult());
            await Task.WhenAny(Result.Task, canceled.Task);
            cancellationToken.ThrowIfCancellationRequested();
            return (await Result.Task)!;
        }
    }

    /// <summary>セルの自動導入ONと設定非依存の複数行本体取得で、初回要求からDriveのキーを保持し、完全な保存bytesを一回だけ導入へ渡すことを確認します。</summary>
    /// <remarks>実workspaceの行解決・取得・保存・引渡しを接続し、通信だけを固定応答へ置換します。四URL形式の条件網羅と実WPF配送は既存の専用ケースへ分担します。</remarks>
    [TestMethod]
    [DataRow(false)] // セルは自動導入と起動時ファイル検査を有効にします。
    [DataRow(true)] // 複数行は両設定が無効でも本体URLを取得します。
    public async Task GoogleDriveResourceKey_FirstRequestSavesAndHandsOffFromSingleAndBulk(bool bulk)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            const string resourceKey = "key+/%2F&=?#";
            Uri[] sources =
            [
                new("https://drive.google.com/file/d/FirstId/view?resourcekey=key%2B%2F%252F%26%3D%3F%23"),
                new("https://drive.google.com/open?id=SecondId&resourcekey=key%2B%2F%252F%26%3D%3F%23")
            ];
            string[] ids = ["FirstId", "SecondId"];
            byte[][] contents = [[1, 2, 3], [4, 5, 6]];
            int expectedCount = bulk ? 2 : 1;
            var gateway = new RecordingPlaylistUrlDownloadGateway(root)
            {
                ResponseFactory = uri =>
                {
                    // 期待値を本番の正規化結果から作らず、最初に渡された要求を独立に読みます。
                    NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
                    Assert.AreEqual("drive.usercontent.google.com", uri.Host);
                    Assert.AreEqual("/download", uri.AbsolutePath);
                    Assert.AreEqual("download", query["export"]);
                    Assert.AreEqual(resourceKey, query["resourcekey"]);
                    int index = Array.IndexOf(ids, query["id"]);
                    Assert.IsTrue(index >= 0);
                    return CreateHttpResponse(uri, contents[index], "application/zip", "attachment; filename=" + ids[index] + ".zip");
                }
            };
            int handoffs = 0;
            int browserCount = 0;
            IReadOnlyList<string>? capturedPaths = null;
            workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = !bulk, AutoInstall = !bulk },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                paths =>
                {
                    handoffs++;
                    capturedPaths = paths;
                    Assert.AreEqual(expectedCount, paths.Count);
                    for (int index = 0; index < paths.Count; index++)
                    {
                        Assert.AreEqual(ids[index] + ".zip", Path.GetFileName(paths[index]));
                        CollectionAssert.AreEqual(contents[index], File.ReadAllBytes(paths[index]));
                    }
                    return true;
                },
                browserSink: _ => browserCount++,
                dialogService: new RecordingPlaylistUrlDialogService());

            if (bulk)
            {
                await workspace.RunPlaylistUrlActionAsync(sources.Select(uri => CreatePlaylistUrlRow(uri.AbsoluteUri, null)), isDiffUrl: false);
            }
            else
            {
                await workspace.RunSinglePlaylistUrlAsync(sources[0]);
            }

            Assert.AreEqual(expectedCount, gateway.RequestedUris.Count);
            CollectionAssert.AreEqual(ids.Take(expectedCount).ToArray(), gateway.RequestedUris.Select(uri => HttpUtility.ParseQueryString(uri.Query)["id"]).ToArray());
            Assert.AreEqual(expectedCount, gateway.OpenWriteCount);
            Assert.AreEqual(1, handoffs);
            Assert.AreEqual(0, browserCount);
            Assert.IsNotNull(capturedPaths);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            for (int index = 0; index < capturedPaths.Count; index++)
            {
                CollectionAssert.AreEqual(contents[index], File.ReadAllBytes(capturedPaths[index]));
            }
        }
        finally
        {
            try
            {
                workspace?.RequestPlaylistUrlShutdown();
                if (workspace != null) { await workspace.WaitForPlaylistUrlIdleAsync(); }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>queryなしURLから実HTTP転送した最終名とbytesを一回だけ変更不能なpathsでsinkへ渡し、その後tree展開します。進捗・診断故障の有無によらず進捗終端と受理後の入力保持を確認し、名前・本文の喪失と順序の逆転を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SingleRedirectedPackage_UsesFinalNameAndInstallSinkBeforeTreeExpansionPresentation(bool failNotifications)
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        List<Task> httpRequests = ownedHttpTasks;
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); ownedHttpTasks.Add(ownedServer.Completion); return ownedServer; }

        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        string temporaryDirectory = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] expectedBytes = [1, 2, 3];
        SingleRequestHttpServer? finalServer = null;
        SingleRequestHttpServer? redirectServer = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Exception? operationFailure = null;
        try
        {
            finalServer = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, expectedBytes));
            redirectServer = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token,
                [],
                responseStatusCode: System.Net.HttpStatusCode.Found,
                additionalResponseHeaders: "Location: " + new Uri(finalServer.Address, "song.zip").AbsoluteUri + "\r\n"));
            var gateway = new BodyGateway(temporaryDirectory, Stream.Null) { RealHttp = true };
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ =>
            {
                if (failNotifications) { throw new IOException("download diagnostic unavailable"); }
            });
            var events = new List<string>();
            IReadOnlyList<string>? capturedPaths = null;
            byte[]? capturedBytes = null;
            bool inputsReadyAtHandoff = false;
            int browserCount = 0;
            workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                    inputsReadyAtHandoff = paths.All(File.Exists);
                    capturedBytes = paths.Count == 1 ? File.ReadAllBytes(paths[0]) : null;
                    return true;
                },
                browserSink: _ => browserCount++,
                treeExpansionSink: () => events.Add("expanded"));

            var statuses = new List<PlaylistUrlDownloadStatusSnapshot>();
            workspace.PlaylistUrlDownloadStatusChanged += (_, _) =>
            {
                if (failNotifications) { throw new IOException("progress unavailable"); }
            };
            workspace.PlaylistUrlDownloadStatusChanged += (_, status) => statuses.Add(status);

            await SingleRequestHttpServer.AwaitOperationAsync(workspace.RunSinglePlaylistUrlAsync(new Uri(redirectServer.Address, "download/123")), httpRequests, httpOperation.Token);

            CollectionAssert.AreEqual(new[] { "sink", "expanded" }, events);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            Assert.IsTrue(statuses.Exists(status => status.IsActive));
            Assert.IsFalse(statuses[^1].IsActive);
            Assert.IsTrue(inputsReadyAtHandoff);
            Assert.IsNotNull(capturedPaths);
            Assert.AreEqual(1, capturedPaths.Count);
            Assert.AreEqual("song.zip", Path.GetFileName(capturedPaths[0]));
            Assert.IsNotNull(capturedBytes);
            CollectionAssert.AreEqual(expectedBytes, capturedBytes);
            CollectionAssert.AreEqual(new[] { "/download/123" }, redirectServer.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/song.zip" }, finalServer.RequestTargets);
            await (redirectServer.Completion).WaitAsync(httpOperation.Token);
            await (finalServer.Completion).WaitAsync(httpOperation.Token);
            Assert.AreEqual(0, browserCount);
            Assert.IsTrue(((IList<string>)capturedPaths).IsReadOnly);
            Assert.IsTrue(File.Exists(capturedPaths[0]), "受理後の入力は導入側の寿命へ移します。");
            CollectionAssert.AreEqual(expectedBytes, File.ReadAllBytes(capturedPaths[0]));
        }
        catch (Exception failure) { operationFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            workspace?.RequestPlaylistUrlShutdown();
            if (workspace != null) { httpRequests.Add(workspace.WaitForPlaylistUrlIdleAsync()); }
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, operationFailure, httpRequests, httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {
                var cleanupFailures = new List<Exception>();
                try { httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(temporaryDirectory, recursive: true); migrationWorkspace.Dispose(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
                if (cleanupFailures.Count > 0)
                {
                    if (operationFailure != null) { cleanupFailures.Insert(0, operationFailure); }
                    throw new AggregateException(cleanupFailures);
                }

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (operationFailure != null) { throw new AggregateException(operationFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    /// <summary>同じoriginのpath caseだけ違う二URLを実HTTPで取得し、別本文を変更不能なpathsで一回sinkへ渡します。通知故障の有無によらずsink→tree展開→結果表示、進捗終端、受理後の本文保持を確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BulkDownloadedPackages_UsesCopiedInstallSnapshotBeforeTreeExpansionAndSummary(bool failNotifications)
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        List<Task> httpRequests = ownedHttpTasks;
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); ownedHttpTasks.Add(ownedServer.Completion); return ownedServer; }

        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        string temporaryDirectory = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        PlaylistWorkspaceViewModel? workspace = null;
        Exception? operationFailure = null;
        try
        {
            SingleRequestHttpServer server = OwnHttpServer(SingleRequestHttpServer.StartFiniteResponses(httpOperation.Token, new SingleRequestHttpServer.Response[]
            {
                new("/Song.zip", [1, 2, 3], "Content-Disposition: attachment; filename=first.zip\r\n"),
                new("/song.zip", [4, 5, 6], "Content-Disposition: attachment; filename=second.zip\r\n")
            }));
            var gateway = new BodyGateway(temporaryDirectory, Stream.Null) { RealHttp = true };
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ =>
            {
                if (failNotifications) { throw new IOException("download diagnostic unavailable"); }
            });
            var events = new List<string>();
            IReadOnlyList<string>? capturedPaths = null;
            byte[][]? capturedBytes = null;
            bool inputsReadyAtHandoff = false;
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                MessageObserver = _ =>
                {
                    events.Add("summary");
                    if (failNotifications) { throw new IOException("summary unavailable"); }
                }
            };
            workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot(),
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                    inputsReadyAtHandoff = paths.All(File.Exists);
                    capturedBytes = paths.Select(File.ReadAllBytes).ToArray();
                    return true;
                },
                treeExpansionSink: () => events.Add("expanded"),
                dialogService: dialogs);

            var statuses = new List<PlaylistUrlDownloadStatusSnapshot>();
            workspace.PlaylistUrlDownloadStatusChanged += (_, _) =>
            {
                if (failNotifications) { throw new IOException("progress unavailable"); }
            };
            workspace.PlaylistUrlDownloadStatusChanged += (_, status) => statuses.Add(status);

            await SingleRequestHttpServer.AwaitOperationAsync(workspace.RunPlaylistUrlBatchAsync(
                [
                    new Uri(server.Address, "Song.zip"),
                    new Uri(server.Address, "song.zip")
                ],
                isDiffUrl: false), httpRequests, httpOperation.Token);

            CollectionAssert.AreEqual(new[] { "sink", "expanded", "summary" }, events);
            Assert.AreEqual(1, dialogs.Confirmations.Count);
            Assert.AreEqual(1, dialogs.Messages.Count);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            Assert.IsTrue(statuses.Exists(status => status.IsActive));
            Assert.IsFalse(statuses[^1].IsActive);
            Assert.IsTrue(inputsReadyAtHandoff);
            Assert.IsNotNull(capturedPaths);
            Assert.AreEqual(2, capturedPaths.Count);
            Assert.AreNotEqual(capturedPaths[0], capturedPaths[1]);
            Assert.IsNotNull(capturedBytes);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, capturedBytes[0]);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, capturedBytes[1]);
            CollectionAssert.AreEqual(new[] { "/Song.zip", "/song.zip" }, server.RequestTargets);
            await (server.Completion).WaitAsync(httpOperation.Token);
            Assert.IsTrue(((IList<string>)capturedPaths).IsReadOnly);
            for (int index = 0; index < capturedPaths.Count; index++)
            {
                string path = capturedPaths[index];
                Assert.IsTrue(File.Exists(path), "受理後の入力は導入側の寿命へ移します。");
                CollectionAssert.AreEqual(capturedBytes[index], File.ReadAllBytes(path));
            }
        }
        catch (Exception failure) { operationFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            workspace?.RequestPlaylistUrlShutdown();
            if (workspace != null) { httpRequests.Add(workspace.WaitForPlaylistUrlIdleAsync()); }
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, operationFailure, httpRequests, httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {
                var cleanupFailures = new List<Exception>();
                try { httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(temporaryDirectory, recursive: true); migrationWorkspace.Dispose(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
                if (cleanupFailures.Count > 0)
                {
                    if (operationFailure != null) { cleanupFailures.Insert(0, operationFailure); }
                    throw new AggregateException(cleanupFailures);
                }

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (operationFailure != null) { throw new AggregateException(operationFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }









    /// <summary>通常/API双方でquery値のcaseだけ違う二本文を保存し、二件目の応答解放中の取消でも実終端後に一回渡します。三件目の開始、本文喪失、早すぎる引渡しを検出します。</summary>
    [TestMethod]
    [DataRow("api")]
    [DataRow("url")]
    public async Task DownloadedPackages_CancellationDuringResponseDisposalHandsOffCompletedDownloadsOnce(string ingress)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acquisition = null;
        string[] md5s = ["11111111111111111111111111111111", "22222222222222222222222222222222", "33333333333333333333333333333333"];
        Uri[] uris = [new("https://example.invalid/package.zip?token=Song"), new("https://example.invalid/package.zip?token=song"), new("https://example.invalid/third.zip")];
        byte[] firstBytes = [1, 2, 3];
        byte[] secondBytes = [4, 5, 6];
        var firstStream = new DisposalGatedDownloadStream(firstBytes);
        var secondStream = new DisposalGatedDownloadStream(secondBytes, disposeEntered, disposeRelease.Task);
        try
        {
            var provider = new BatchHandoffLookupProvider(md5s.Zip(uris).ToDictionary(pair => pair.First, pair => pair.Second));
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(uris[0], () => CreateNamedStreamResponse(uris[0], firstStream, "first.zip"));
            gateway.AddResponse(uris[1], () => CreateNamedStreamResponse(uris[1], secondStream, "second.zip"));
            gateway.AddResponse(uris[2], () => new AppHttpResponse(uris[2], new MemoryStream([7, 8, 9], writable: false)));
            int handoffs = 0;
            IReadOnlyList<string>? capturedPaths = null;
            byte[][]? capturedBytes = null;
            bool responsesDisposedAtHandoff = false;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot(),
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                paths =>
                {
                    handoffs++;
                    capturedPaths = paths;
                    capturedBytes = paths.Select(File.ReadAllBytes).ToArray();
                    responsesDisposedAtHandoff = firstStream.WasDisposed && secondStream.WasDisposed;
                    return true;
                },
                dialogService: new RecordingPlaylistUrlDialogService(),
                externalLookupService: new PlaylistExternalPackageLookupService([provider]));

            acquisition = Task.Run(() => ingress == "api"
                ? workspace.RunPlaylistExternalPackageLookupAsync(md5s.Select(CreatePlaylistExternalPackageRow))
                : workspace.RunPlaylistUrlBatchAsync(uris, isDiffUrl: false));
            Task reached = await Task.WhenAny(disposeEntered.Task, acquisition);
            if (reached == acquisition) { await acquisition; }
            Assert.AreSame(disposeEntered.Task, reached);
            CollectionAssert.AreEqual(secondBytes, File.ReadAllBytes(Path.Combine(root, "second.zip")));
            Assert.IsTrue(firstStream.WasDisposed);
            Assert.IsFalse(secondStream.WasDisposed);

            workspace.CancelPlaylistUrlDownload();

            Assert.IsFalse(acquisition.IsCompleted, "開始済みの応答解放が終わるまで取得操作を保持します。");
            Assert.AreEqual(0, handoffs);
            CollectionAssert.AreEqual(ingress == "api" ? md5s.Take(2).ToArray() : [], provider.RequestedMd5s);
            disposeRelease.TrySetResult();
            await acquisition;

            Assert.AreEqual(1, handoffs);
            Assert.IsNotNull(capturedPaths);
            Assert.IsNotNull(capturedBytes);
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, capturedPaths.Select(Path.GetFileName).ToArray());
            Assert.AreEqual(2, capturedBytes.Length);
            CollectionAssert.AreEqual(firstBytes, capturedBytes[0]);
            CollectionAssert.AreEqual(secondBytes, capturedBytes[1]);
            Assert.IsTrue(responsesDisposedAtHandoff);
            Assert.IsTrue(secondStream.WasDisposed);
            CollectionAssert.AreEqual(ingress == "api" ? md5s.Take(2).ToArray() : [], provider.RequestedMd5s);
            CollectionAssert.AreEqual(uris.Take(2).ToArray(), gateway.RequestedUris);
            Assert.IsTrue(capturedPaths.All(File.Exists), "受理後の入力は導入側へ寿命を移します。");
            Assert.AreEqual(2, Directory.GetFiles(root).Length);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
        }
        finally
        {
            disposeRelease.TrySetResult();
            try { if (acquisition != null) { await acquisition; } }
            finally
            {
                firstStream.Dispose();
                secondStream.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 単体列・本体/差分取り込み・外部 API の取得成功後に共通受付が Busy の場合、
    /// 警告で終わり、導入・tree 展開・ブラウザ fallback・後続の自動再実行を行いません。
    /// 通信段階ではライブラリ変更受付を占有しない契約を、実 queue への引渡しで検証します。
    /// </summary>
    [TestMethod]
    // 単独列、本体一括、差分一括、外部API検索で同じBusy引渡しを確認する。
    [DataRow("single")]
    [DataRow("url")]
    [DataRow("diff")]
    [DataRow("api")]
    public async Task DownloadedPackages_BusyHandoffWarnsWithoutFallbackOrReplay(string ingress)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var gate = new ChartFileOperationSynchronizer();
        IDisposable? competingOperation = null;
        PackageInstallWorkflowOwner? installOwner = null;
        try
        {
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            Settings headlessLibrarySettings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, string.Empty, () => BmsLibraryOptionsSnapshot.CreateCurrent(headlessLibrarySettings), TestBmsFactory.MissingEverythingBridge, uiScheduler: new TestUiScheduler(() => null), settings: headlessLibrarySettings);
            int installCount = 0;
            installOwner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(), gate, new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, _, _, _) =>
                {
                    installCount++;
                    return new PackageInstallCommandResult([], null);
                }), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
            installOwner.AttachLibrary(library);
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            Uri first = new("https://example.invalid/first.zip");
            Uri second = new("https://example.invalid/second.zip");
            foreach (Uri uri in new[] { first, second })
            {
                gateway.AddResponse(uri, () =>
                {
                    // 通信中に通常の変更要求が受理され、取得完了時も受付を保持している。
                    if (competingOperation == null)
                    {
                        Assert.IsTrue(gate.TryEnter(out IDisposable lease));
                        competingOperation = lease;
                    }
                    return new AppHttpResponse(uri, new MemoryStream([1, 2, 3], writable: false));
                });
            }
            var dialogs = new RecordingPlaylistUrlDialogService();
            int handoffCount = 0;
            int browserCount = 0;
            int expansionCount = 0;
            IReadOnlyList<string>? downloaded = null;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope,
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: request =>
                {
                    handoffCount++;
                    downloaded = Array.AsReadOnly(request.Paths);
                    return installOwner.TryEnqueue(request);
                },
                browserSink: _ => browserCount++,
                treeExpansionSink: () => expansionCount++,
                dialogService: dialogs,
                externalLookupService: new PlaylistExternalPackageLookupService([new HandoffLookupProvider(first)]));
            if (ingress == "api")
            {
                await workspace.RunPlaylistExternalPackageLookupAsync(
                    [CreatePlaylistExternalPackageRow("11111111111111111111111111111111")]);
            }
            else if (ingress == "single")
            {
                await workspace.RunSinglePlaylistUrlAsync(first);
            }
            else
            {
                await workspace.RunPlaylistUrlActionAsync(
                    [CreatePlaylistUrlRow(first.AbsoluteUri, second.AbsoluteUri),
                     CreatePlaylistUrlRow(second.AbsoluteUri, first.AbsoluteUri)],
                    isDiffUrl: ingress == "diff");
            }

            Assert.AreEqual(1, handoffCount);
            Assert.AreEqual(0, installCount);
            Assert.AreEqual(0, browserCount);
            Assert.AreEqual(0, expansionCount);
            Assert.IsFalse(installOwner.IsActive);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            Assert.IsNotNull(downloaded);
            string[] expectedNames = ingress switch
            {
                "url" => ["first.zip", "second.zip"],
                "diff" => ["second.zip", "first.zip"],
                _ => ["first.zip"]
            };
            CollectionAssert.AreEqual(expectedNames, downloaded.Select(Path.GetFileName).ToArray());
            Assert.IsTrue(downloaded.All(path => !File.Exists(path)), "未受理入力は取得側が回収を待ちます。");
            Assert.AreEqual(1, dialogs.Messages.Count(message => message.Icon == MessageBoxImage.Warning));
            Assert.IsFalse(dialogs.Messages.Any(message => message.Icon == MessageBoxImage.Error));
            Assert.AreEqual(ingress == "single" ? 1 : 2, dialogs.Messages.Count);
            Assert.IsNotNull(competingOperation);
            competingOperation.Dispose();
            competingOperation = null;
            await installOwner.WaitForIdleAsync();
            Assert.AreEqual(1, handoffCount, "拒否した要求を保存・再試行しない。");
            Assert.AreEqual(0, installCount);
            Assert.IsTrue(gate.TryEnter(out IDisposable afterRejection));
            afterRejection.Dispose();
        }
        finally
        {
            competingOperation?.Dispose();
            if (installOwner != null)
            {
                await installOwner.WaitForIdleAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>実導入ownerへの一回引渡しと入力使用終端を通し、P中の読取り取得と開始後のstaging保持を確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadedInput_HoldsStagingUntilActualInstallTerminalWhilePlaylistIsBusy(bool failAfterStart)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acquisition = null;
        IDisposable? playlistLease = null;
        PackageInstallWorkflowOwner? installer = null;
        PackageInstallFailure? installFailure = null;
        var expectedFailure = new IOException("started install failure");
        string? capturedPath = null;
        int handoffs = 0;
        try
        {
            string db = Path.Combine(root, "song.db");
            File.WriteAllBytes(db, []);
            Settings headlessLibrarySettings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
            var library = new TestBmsLibrary(databaseScope, db, null, null, string.Empty, () => BmsLibraryOptionsSnapshot.CreateCurrent(headlessLibrarySettings), TestBmsFactory.MissingEverythingBridge, uiScheduler: new TestUiScheduler(() => null), settings: headlessLibrarySettings);
            installer = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(),
                library.OperationAdmission, new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, paths, _, _) =>
                {
                    Assert.IsTrue(File.Exists(capturedPath));
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    Assert.IsTrue(File.Exists(capturedPath));
                    if (failAfterStart) { throw expectedFailure; }
                    return new PackageInstallCommandResult([], null);
                }), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
            installer.AttachLibrary(library);
            installer.FailurePublished += failure => installFailure = failure;
            var gateway = new FakePlaylistUrlDownloadGateway(root, [1, 2, 3]);
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: request => { handoffs++; capturedPath = request.Paths.Single(); return installer.TryEnqueue(request); },
                installCompletion: installer.WaitForIdleAsync,
                captureStore: store =>
                {
                    Assert.IsTrue(store.TryEnterPlaylistMutation(out IDisposable lease));
                    playlistLease = lease;
                });
            acquisition = workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/owned.zip"));
            Task reached = await Task.WhenAny(entered.Task, acquisition);
            if (reached == acquisition) { await acquisition; }
            Assert.AreSame(entered.Task, reached);
            Assert.IsFalse(acquisition.IsCompleted);
            Assert.IsTrue(File.Exists(capturedPath));
            Assert.AreEqual(1, handoffs);
            release.TrySetResult();
            await acquisition;
            Assert.IsTrue(File.Exists(capturedPath));
            Assert.IsFalse(library.OperationAdmission.IsActive);
            Assert.AreEqual(1, handoffs);
            Assert.AreSame(failAfterStart ? expectedFailure : null, installFailure?.Exception);
        }
        finally
        {
            release.TrySetResult();
            try { if (acquisition != null) { await acquisition; } }
            finally
            {
                if (installer != null) { await installer.WaitForIdleAsync(); }
                playlistLease?.Dispose();
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>URLが作った所有要求を実queueへ挿入し、worker開始前の取消・世代退役で入力回収と実idleを待ちます。所有情報の付与漏れと早いidleを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadedInput_CancellationBeforeStartReclaimsOwnedSourceBeforeIdle(bool retireGeneration)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "url-before-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var statusEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acquisition = null;
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            string db = Path.Combine(root, "song.db");
            File.WriteAllBytes(db, []);
            Settings headlessLibrarySettings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
            var library = new TestBmsLibrary(databaseScope, db, null, null, string.Empty, () => BmsLibraryOptionsSnapshot.CreateCurrent(headlessLibrarySettings), TestBmsFactory.MissingEverythingBridge, uiScheduler: new TestUiScheduler(() => null), settings: headlessLibrarySettings);
            int installs = 0;
            owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), library.OperationAdmission,
                new ChartMutationActivityOwner(), new DelegatePackageInstallMutationPort((_, _, _, _) =>
                {
                    Interlocked.Increment(ref installs);
                    return new PackageInstallCommandResult([], null);
                }), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
            owner.AttachLibrary(library);
            int held = 0;
            owner.StatusChanged += snapshot =>
            {
                if (snapshot.IsActive && Interlocked.Exchange(ref held, 1) == 0)
                {
                    statusEntered.TrySetResult();
                    releaseStatus.Task.GetAwaiter().GetResult();
                }
            };
            Uri uri = new("https://example.invalid/owned.bms");
            var gateway = new RecordingPlaylistUrlDownloadGateway(root)
            {
                BeforeDelete = () => { cleanupEntered.TrySetResult(); releaseCleanup.Task.GetAwaiter().GetResult(); }
            };
            gateway.AddResponse(uri, () => new AppHttpResponse(uri, new MemoryStream([1, 2, 3], writable: false)));
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: owner.TryEnqueue, installCompletion: owner.WaitForIdleAsync);
            acquisition = Task.Run(() => workspace.RunSinglePlaylistUrlAsync(uri));
            await TestUiDispatcherHost.AwaitNotificationAsync(statusEntered.Task, acquisition, "URL queue insertion");
            if (retireGeneration)
            {
                Settings replacementLibrarySettings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
                owner.AttachLibrary(new TestBmsLibrary(databaseScope, db, null, null, string.Empty, () => BmsLibraryOptionsSnapshot.CreateCurrent(replacementLibrarySettings), TestBmsFactory.MissingEverythingBridge, uiScheduler: new TestUiScheduler(() => null), settings: replacementLibrarySettings));
            }
            else { owner.CancelAll(); }
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupEntered.Task, acquisition, "URL unconsumed cleanup");
            Assert.AreEqual(0, installs);
            Assert.IsTrue(File.Exists(Path.Combine(root, "owned.bms")));
            Assert.IsFalse(acquisition.IsCompleted);
            Assert.IsFalse(owner.WaitForIdleAsync().IsCompleted);
            releaseStatus.TrySetResult();
            releaseCleanup.TrySetResult();
            await acquisition;
            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, installs);
            Assert.AreEqual(1, gateway.DeleteCount);
            Assert.IsFalse(File.Exists(Path.Combine(root, "owned.bms")));
            Assert.IsTrue(owner.Enqueue([Path.Combine(root, "next-borrowed.zip")]));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, installs);
        }
        finally
        {
            releaseStatus.TrySetResult();
            releaseCleanup.TrySetResult();
            try { if (acquisition != null) { await acquisition; } }
            finally
            {
                if (owner != null) { await owner.WaitForIdleAsync(); }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>未受理と受理前故障は要求自身が一回だけ回収し、取得Taskが実削除を待ちます。元故障と回収失敗を両方保持し、自動再試行しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadedInput_UnacceptedWaitsForCleanupAndPreservesBothFailures(bool failHandoffAndCleanup)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "url-rejected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acquisition = null;
        Exception? observed = null;
        var handoffFailure = new IOException("handoff failure");
        var cleanupFailure = new IOException("cleanup failure");
        try
        {
            Uri uri = new("https://example.invalid/rejected.bms");
            var gateway = new RecordingPlaylistUrlDownloadGateway(root)
            {
                BeforeDelete = () => { cleanupEntered.TrySetResult(); releaseCleanup.Task.GetAwaiter().GetResult(); },
                DeleteFailure = failHandoffAndCleanup ? cleanupFailure : null
            };
            gateway.AddResponse(uri, () => new AppHttpResponse(uri, new MemoryStream([1, 2, 3], writable: false)));
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: _ => failHandoffAndCleanup ? throw handoffFailure : false,
                dialogService: new RecordingPlaylistUrlDialogService());
            acquisition = workspace.RunSinglePlaylistUrlAsync(uri);
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupEntered.Task, acquisition, "URL rejected cleanup");
            Assert.IsFalse(acquisition.IsCompleted);
            releaseCleanup.TrySetResult();
            try { await acquisition; } catch (Exception failure) { observed = failure; }
            Assert.AreEqual(1, gateway.DeleteCount);
            Assert.AreEqual(failHandoffAndCleanup, File.Exists(Path.Combine(root, "rejected.bms")));
            if (failHandoffAndCleanup)
            {
                var aggregate = observed as AggregateException;
                Assert.IsNotNull(aggregate);
                CollectionAssert.AreEqual(new Exception[] { handoffFailure, cleanupFailure }, aggregate.Flatten().InnerExceptions.ToArray());
            }
            else { Assert.IsNull(observed); }
        }
        finally
        {
            releaseCleanup.TrySetResult();
            try
            {
                if (acquisition != null)
                {
                    try { await acquisition; }
                    catch (AggregateException failure) when (failHandoffAndCleanup
                        && failure.Flatten().InnerExceptions.Contains(handoffFailure)
                        && failure.Flatten().InnerExceptions.Contains(cleanupFailure))
                    { }
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>取得先を固定し、外部API検索ownerの実引渡し経路を通すprovider。</summary>
    private sealed class HandoffLookupProvider(Uri downloadUri) : IPlaylistExternalPackageLookupProvider
    {
        public string ProviderId => "handoff-test";

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
            => Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUri));
    }

    // 保存成功後の通常の応答解放を止め、外からの取消要求と実終端の順序を固定します。
    private sealed class DisposalGatedDownloadStream(byte[] content, TaskCompletionSource? entered = null, Task? release = null)
        : MemoryStream(content, writable: false)
    {
        internal bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                entered?.TrySetResult();
                release?.GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
            WasDisposed = true;
        }
    }

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">同じappが使うプレイリストのDB所有者。</param>
    private PlaylistWorkspaceViewModel CreateWorkspace(ApplicationDatabaseScope databaseScope,
        Action<Action> dispatch,
        Func<PlaylistUrlAcquisitionOptionsSnapshot>? optionsProvider = null,
        PlaylistUrlAcquisitionWorkflow? acquisitionWorkflow = null,
        Func<IReadOnlyList<string>, bool>? installSink = null,
        Action<Uri>? browserSink = null,
        Action? treeExpansionSink = null,
        IUiDialogService? dialogService = null,
        Func<Action, Task>? presentationScheduler = null,
        PlaylistExternalPackageLookupService? externalLookupService = null,
        Func<Task>? installCompletion = null, Action<BMSPlaylist>? captureStore = null,
        Func<DroppedInstallBatchRequest, bool>? ownedInstallSink = null)
    {
        PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedPlaylistStore =
            PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore(databaseScope, modelUiScheduler: new TestUiScheduler(() => null));
        ownedPlaylistStores.Add(ownedPlaylistStore);
        captureStore?.Invoke(ownedPlaylistStore.Store);
        Func<Action, Task> urlPresentationScheduler = presentationScheduler
            ?? (action =>
            {
                dispatch(action);
                return Task.CompletedTask;
            });
        Action actualTreeExpansionSink = treeExpansionSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink;
        var workspace = new PlaylistWorkspaceViewModel(
            dispatch,
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            acquisitionWorkflow ?? PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
            externalLookupService ?? PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
            optionsProvider ?? PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            ownedInstallSink ?? (installSink == null ? PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink : request => installSink(Array.AsReadOnly(request.Paths))),
            browserSink ?? PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
            () => ownedPlaylistStore.Store,
            PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, urlPresentationScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, dialogService ?? UndeclaredDialogs.Create(), playlistUrlInstallCompletionProvider: installCompletion);
        workspace.PlaylistUrlInstallTreeExpansionRequested += actualTreeExpansionSink;
        return workspace;
    }

    /// <summary>queryキー/値、共有IDのcase差は二本文を保存し、同一実体のhost/fragment・共有別表記・resourcekeyや揮発tokenの差は二重保存しません。要求履歴と実FSで成功集合の誤分離を検出します。</summary>
    [TestMethod]
    [DataRow("https://example.invalid/package.zip?Token=a", "https://example.invalid/package.zip?token=a", false)]
    [DataRow("https://example.invalid/package.zip?token=Song", "https://example.invalid/package.zip?token=song", false)]
    [DataRow("https://drive.google.com/file/d/AbC/view", "https://drive.google.com/open?id=abc", false)]
    [DataRow("https://www.mediafire.com/file/AbC/package.zip/file", "https://download123.mediafire.com/token/abc/package.zip", false)]
    [DataRow("HTTPS://EXAMPLE.invalid/package.zip#first", "https://example.invalid/package.zip#second", true)]
    [DataRow("https://drive.google.com/file/d/AbC/view?resourcekey=first", "https://drive.usercontent.google.com/download?id=AbC&resourcekey=second&confirm=t&uuid=volatile", true)]
    [DataRow("https://www.mediafire.com/file/AbC/package.zip/file", "https://download123.mediafire.com/other/AbC/package.zip", true)]
    public async Task DownloadCandidate_PreservesSignificantUrlCaseAndSuppressesSameFile(string firstUrl, string secondUrl, bool duplicate)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var expectedRequests = new List<string>();
            var results = new PlaylistUrlDownloadResult[2];
            string[] urls = [firstUrl, secondUrl];
            byte[][] contents = [[1, 2, 3], [4, 5, 6]];
            for (int index = 0; index < urls.Length; index++)
            {
                Uri input = new(urls[index]);
                Uri normalized = PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(input);
                Uri file = normalized;
                if (normalized.Host == "www.mediafire.com")
                {
                    file = new Uri("https://download123.mediafire.com/token/" + normalized.Segments[2].TrimEnd('/') + "/package.zip");
                    string html = "<a id=\"downloadButton\" href=\"" + file.AbsoluteUri + "\">Download</a>";
                    gateway.AddResponse(normalized, () => CreateHttpResponse(normalized, Encoding.UTF8.GetBytes(html), "text/html"));
                    expectedRequests.Add(normalized.AbsoluteUri);
                }
                byte[] bytes = contents[index];
                string name = index == 0 ? "first.zip" : "second.zip";
                gateway.AddResponse(file, () => CreateHttpResponse(file, bytes, "application/zip", "attachment; filename=" + name));
                expectedRequests.Add(file.AbsoluteUri);
                results[index] = await workflow.DownloadCandidateAsync(input, keys);
            }

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, results[0].Kind);
            Assert.AreEqual(duplicate ? PlaylistUrlDownloadResultKind.Duplicate : PlaylistUrlDownloadResultKind.Downloaded, results[1].Kind);
            CollectionAssert.AreEqual(contents[0], File.ReadAllBytes(results[0].FilePath));
            if (!duplicate)
            {
                Assert.AreNotEqual(results[0].FilePath, results[1].FilePath);
                CollectionAssert.AreEqual(contents[1], File.ReadAllBytes(results[1].FilePath));
            }
            Assert.AreEqual(duplicate ? 1 : 2, gateway.OpenWriteCount);
            CollectionAssert.AreEqual(expectedRequests, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>MediaFireのcase別遷移は保存へ進み、fragmentだけ変えて元へ戻る循環は二要求で止めます。深さ上限による終了と誤同一視を要求順で区別します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadCandidate_SharedPageDistinguishesCaseAndStopsFragmentCycle(bool cycle)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Uri first = new("https://www.mediafire.com/file/AbC/package.zip/file");
        Uri second = new("https://www.mediafire.com/file/abc/package.zip/file");
        Uri file = new("https://download123.mediafire.com/token/abc/package.zip");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(first, () => CreateHttpResponse(first, Encoding.UTF8.GetBytes("<a id=\"downloadButton\" href=\"" + second + "\">Download</a>"), "text/html"));
            Uri target = cycle ? new Uri("https://WWW.MEDIAFIRE.COM/file/AbC/package.zip/file#again") : file;
            gateway.AddResponse(second, () => CreateHttpResponse(second, Encoding.UTF8.GetBytes("<a id=\"downloadButton\" href=\"" + target + "\">Download</a>"), "text/html"));
            gateway.AddResponse(file, () => CreateHttpResponse(file, [7, 8, 9], "application/zip"));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(first);

            Assert.AreEqual(cycle ? PlaylistUrlDownloadResultKind.BrowserFallback : PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            CollectionAssert.AreEqual(cycle ? new[] { first.AbsoluteUri, second.AbsoluteUri } : new[] { first.AbsoluteUri, second.AbsoluteUri, file.AbsoluteUri }, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            Assert.AreEqual(cycle ? 0 : 1, gateway.OpenWriteCount);
            if (!cycle) { CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(result.FilePath)); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>venue非COREのhost/fragment別表記を除外したまま、caseだけ違う通常anchorの本体を保存します。非CORE要求の復活と別本体の巻込みを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_NonCoreExclusionPreservesCaseDistinctAnchor()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Uri page = new("https://venue.bmssearch.net/freebattle/30");
        Uri file = new("https://example.invalid/song.zip");
        try
        {
            string html = "<script>{\"downloadURL\":\"https://example.invalid/Song.zip\",\"type\":\"PACKAGE\"}</script>"
                + "<a href=\"HTTPS://EXAMPLE.INVALID/Song.zip#again\">Excluded</a><a href=\"" + file + "\">Download</a>";
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(page, () => CreateHttpResponse(page, Encoding.UTF8.GetBytes(html), "text/html"));
            gateway.AddResponse(file, () => CreateHttpResponse(file, [7, 8, 9], "application/zip"));

            PlaylistUrlDownloadResult result = await new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }).DownloadCandidateAsync(page);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            CollectionAssert.AreEqual(new[] { page.AbsoluteUri, file.AbsoluteUri }, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(result.FilePath));
            Assert.AreEqual(1, gateway.OpenWriteCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>workspace所有の失敗集合は同実体の再取得を止め、別MD5から得たcase別候補を塞ぎません。実workflow/FSと一回sinkで成功本文を確認します。</summary>
    [TestMethod]
    public async Task ExternalPackageLookup_FailedUrlSuppressesOnlySameCaseFile()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Uri failed = new("https://example.invalid/Song.zip");
        Uri same = new("HTTPS://EXAMPLE.INVALID/Song.zip#again");
        Uri success = new("https://example.invalid/song.zip");
        string[] md5s = ["11111111111111111111111111111111", "22222222222222222222222222222222", "33333333333333333333333333333333"];
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(failed, () => throw new HttpRequestException("first candidate failed"));
            gateway.AddResponse(success, () => CreateHttpResponse(success, [7, 8, 9], "application/zip"));
            var provider = new BatchHandoffLookupProvider(md5s.Zip(new[] { failed, same, success }).ToDictionary(pair => pair.First, pair => pair.Second));
            int handoffs = 0;
            IReadOnlyList<string>? capturedPaths = null;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(), () => new PlaylistUrlAcquisitionOptionsSnapshot(),
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), paths =>
                {
                    handoffs++;
                    capturedPaths = paths;
                    Assert.AreEqual(1, paths.Count);
                    CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(paths[0]));
                    return true;
                }, dialogService: new RecordingPlaylistUrlDialogService(),
                externalLookupService: new PlaylistExternalPackageLookupService([provider]));

            await workspace.RunPlaylistExternalPackageLookupAsync(md5s.Select(CreatePlaylistExternalPackageRow));

            CollectionAssert.AreEqual(md5s, provider.RequestedMd5s);
            CollectionAssert.AreEqual(new[] { failed.AbsoluteUri, success.AbsoluteUri }, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            Assert.AreEqual(1, handoffs);
            Assert.IsNotNull(capturedPaths);
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(capturedPaths[0]));
            Assert.AreEqual(1, gateway.OpenWriteCount);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AppHttpResponse CreateNamedStreamResponse(Uri uri, Stream stream, string name)
    {
        var message = new HttpResponseMessage { Content = new ByteArrayContent([]) };
        message.Content.Headers.ContentLength = stream.Length;
        message.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = name };
        return new AppHttpResponse(uri, message, stream);
    }

    private static AppHttpResponse CreateHttpResponse(
        Uri requestUri,
        byte[] content,
        string contentType,
        string? contentDisposition = null,
        Uri? responseUri = null,
        Uri? location = null)
    {
        var responseMessage = new HttpResponseMessage
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, responseUri ?? requestUri),
            Content = new ByteArrayContent(content)
        };
        responseMessage.Headers.Location = location;
        responseMessage.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            responseMessage.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                contentDisposition);
        }
        return new AppHttpResponse(
            requestUri,
            responseMessage,
            new MemoryStream(content, writable: false));
    }

    // 実ファイルへの最初の書込み後、読取りまたは応答の解放を失敗させます。
    private sealed class FailingDownloadStream(IOException failure, bool failOnDispose, IOException? additionalDisposeFailure = null) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        private int readCount;
        internal bool WasDisposed { get; private set; }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!failOnDispose && readCount++ > 0) { throw failure; }
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            WasDisposed = true;
            if (disposing && additionalDisposeFailure != null) { throw additionalDisposeFailure; }
            if (disposing && failOnDispose) { throw failure; }
        }
    }

    private sealed class FakePlaylistUrlDownloadGateway : IPlaylistUrlDownloadGateway
    {
        private readonly string temporaryDirectory;

        private readonly byte[] content;

        internal FakePlaylistUrlDownloadGateway(string temporaryDirectory, byte[] content)
        {
            this.temporaryDirectory = temporaryDirectory;
            this.content = content;
        }

        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.FromResult<AppHttpResponse>(new AppHttpResponse(uri, new MemoryStream(content, writable: false)));
        }

        public string GetTemporaryDirectory() => temporaryDirectory;

        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share)
        {
            return new FileStream(path, mode, access, share);
        }

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path) => File.Delete(path);
    }

    private sealed class BlockingPlaylistUrlDownloadGateway : IPlaylistUrlDownloadGateway
    {
        private readonly string temporaryDirectory;

        internal BlockingPlaylistUrlDownloadGateway(string temporaryDirectory)
        {
            this.temporaryDirectory = temporaryDirectory;
        }

        internal TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<AppHttpResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            return Response.Task;
        }

        public string GetTemporaryDirectory() => temporaryDirectory;

        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share)
        {
            return new FileStream(path, mode, access, share);
        }

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path) => File.Delete(path);
    }

}
