using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

/// <summary>URLの取得判断と実workspaceから導入・表示への配送、受理されなかった入力の回収を確認します。</summary>
/// <remarks>取得は応答portで固定し実stagingを使います。Windowは不要で、compiled購読の接続はMainWindowの代表が担います。所有移転の代表では実ownerを、保留の代表では実推定・SQLite・FSまで通します。</remarks>
[TestClass]
public sealed class PlaylistUrlAcquisitionOwnershipTests
{
    private readonly List<PlaylistWorkspaceTestPorts.OwnedPlaylistStore> ownedPlaylistStores = [];

    /// <summary>要求の終端後に、各ケースが所有した実store領域を逆順で回収します。</summary>
    [TestCleanup]
    public void CleanupOwnedPlaylistStores()
    {
        for (int index = ownedPlaylistStores.Count - 1; index >= 0; index--)
        {
            ownedPlaylistStores[index].Dispose();
        }
        ownedPlaylistStores.Clear();
    }

    /// <summary>対応する各譜面・archive名の応答を実stagingへ保存し、元basename・拡張子・bytesを保持します。取得物を別形式や別名へ変えて導入先へ渡す誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_SupportedChartAndArchiveNamesWriteExactBasenameExtensionAndBytes()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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

    /// <summary>拡張子のないquery URIでは応答のContent-Dispositionを使ってbmson名とbytesを保存します。URIだけで名前を決めて対応ファイルを失う誤りを検出します。</summary>
    [TestMethod]
    public async Task DownloadCandidate_UsesContentDispositionForExtensionlessQueryUri()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] expectedBytes = [0x51, 0x52, 0x53];
        Uri uri = new("https://example.invalid/download?id=42");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            gateway.AddResponse(uri, () => CreateHttpResponse(
                uri,
                expectedBytes,
                "application/octet-stream",
                "attachment; filename=chart.bmson"));
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(uri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            Assert.AreEqual("chart.bmson", Path.GetFileName(result.FilePath));
            Assert.AreEqual(".bmson", Path.GetExtension(result.FilePath));
            CollectionAssert.AreEqual(expectedBytes, File.ReadAllBytes(result.FilePath));
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
    [DataTestMethod]
    // 譜面本体とarchiveの双方を、同じ共有HTML解決経路で確認する。
    [DataRow("chart.bms")]
    [DataRow("package.zip")]
    public async Task DownloadCandidate_ResolvesKnownSharedHtmlPageToDirectSupportedFile(string fileName)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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

    /// <summary>起動走査を切った単独URLは表示dispatchを通してブラウザへ一回配送します。自動導入条件を無視する誤りとUI配送の接続漏れを検出します。</summary>
    [TestMethod]
    public async Task WorkspaceForwardsBrowserFallbackThroughPresentationDispatch()
    {
        int dispatchCount = 0;
        Uri? openedUri = null;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action =>
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
    [DataTestMethod]
    [DataRow("file:///C:/secret.zip")]
    [DataRow("custom+scheme://drive.google.com/file/d/id/package.zip")]
    public async Task WorkspaceDoesNotOpenOrInstallUnsupportedInitialSchemeWhenAutoInstallIsDisabled(string uriText)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        int installCount = 0;
        int browserOpenCount = 0;
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(temporaryDirectory);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkspaceInternalAcquisitionFailureDoesNotOpenBrowser(bool resourceFailure)
    {
        Exception failure = resourceFailure ? new OutOfMemoryException("resource failure") : new InvalidOperationException("internal failure");
        var gateway = new RecordingPlaylistUrlDownloadGateway(Path.GetTempPath()) { TransportException = failure };
        int browserOpenCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
    [DataTestMethod]
    [DataRow("read", false)]
    [DataRow("read", true)]
    [DataRow("dispose", false)]
    [DataRow("dispose", true)]
    [DataRow("read-and-dispose", false)]
    [DataRow("read-and-dispose", true)]
    public async Task DownloadFailureReclaimsUnpublishedInputAndPreservesUnresolvedCleanup(string stage, bool failCleanup)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
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
            var downloadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>ブラウザsinkの実呼出し例外を要求Taskへ返します。表示先の失敗を成功扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task BrowserFallbackSinkExceptionIsNotSuppressed()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
            action => action(),
            browserSink: _ => throw new InvalidOperationException("browser sink failure"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

    /// <summary>表示schedulerの失敗を要求Taskの終端まで伝播します。非同期配送を待たず成功を返す誤りを検出します。</summary>
    [TestMethod]
    public async Task BrowserFallbackPresentationFailureIsPropagatedThroughAwaitableScheduler()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
            action => action(),
            presentationScheduler: _ => Task.FromException(
                new InvalidOperationException("presentation dispatch failure")));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

    /// <summary>一括取得の確認後にブラウザ対象一件の結果通知を届け、statusを非稼働へ戻します。単独ブラウザdispatchへの混入と終端表示漏れを検出します。</summary>
    [TestMethod]
    public async Task BulkBrowserFallbackPublishesSummaryAndReturnsToInactiveState()
    {
        int dispatchCount = 0;
        var dialogs = new RecordingPlaylistUrlDialogService();
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action =>
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
            action => action(),
            dialogService: dialogs);

        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/rejected/")],
            isDiffUrl: false);
        Assert.AreEqual(1, dialogs.Confirmations.Count);

        dialogs.ConfirmationResult = UiDialogResult.Failed(
            new InvalidOperationException("dialog unavailable"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action());

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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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

    /// <summary>実取得の通信待機中は本体・差分URLを操作不可にし、MD5なしの行では外部検索も不可であることを確認します。受理済みdownloadの稼働をURLメニューへ反映しない誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlContextMenuAvailability_DisablesActionsWhileDownloading()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                acquisitionWorkflow: acquisitionWorkflow,
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

    /// <summary>実取得からhubへ進捗行を届け、取消受付後は再取消を不可にして取得終端で行を除去します。進捗購読の接続漏れと終端行の残留を検出します。</summary>
    [TestMethod]
    public async Task PlaylistUrlProgressAttachedHubPublishesActiveCancelAndTerminalStates()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
        Task? acquisition = null;
        try
        {
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                acquisitionWorkflow: acquisitionWorkflow,
                dialogService: dialogs);
            var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
            hub.AttachPlaylistProgressSources(workspace, action => action(), () => false);

            acquisition = workspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/running.zip")],
                isDiffUrl: false);
            await TestUiDispatcherHost.AwaitNotificationAsync(gateway.ReadStarted.Task, acquisition, "url-acquisition.read-started");

            Assert.IsTrue(hub.Rows.Any(row => row.Key == "url"));
            Assert.IsTrue(hub.Rows.Single(row => row.Key == "url").CanCancel);
            workspace.CancelPlaylistUrlDownload();
            Assert.IsFalse(hub.Rows.Single(row => row.Key == "url").CanCancel);

            gateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/running.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            await acquisition;

            Assert.IsFalse(hub.Rows.Any(row => row.Key == "url"));
            Assert.IsFalse(hub.HasRows);
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

    /// <summary>単独の実取得物を変更不能なpathsでsinkへ渡してからtree展開し、受理後のstagingを保持します。進捗・診断故障を重ねても引渡しを保ち、展開順序の逆転と入力回収漏れを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SingleDownloadedPackage_UsesInstallSinkBeforeTreeExpansionPresentation(bool failNotifications)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new FakePlaylistUrlDownloadGateway(temporaryDirectory, new byte[] { 1, 2, 3 });
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ =>
            {
                if (failNotifications) { throw new IOException("download diagnostic unavailable"); }
            });
            var events = new List<string>();
            IReadOnlyList<string>? capturedPaths = null;
            bool inputsReadyAtHandoff = false;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                    inputsReadyAtHandoff = paths.All(File.Exists);
                    return true;
                },
                treeExpansionSink: () => events.Add("expanded"));

            var statuses = new List<PlaylistUrlDownloadStatusSnapshot>();
            workspace.PlaylistUrlDownloadStatusChanged += (_, _) =>
            {
                if (failNotifications) { throw new IOException("progress unavailable"); }
            };
            workspace.PlaylistUrlDownloadStatusChanged += (_, status) => statuses.Add(status);

            await workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/single.zip"));

            CollectionAssert.AreEqual(new[] { "sink", "expanded" }, events);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            Assert.IsTrue(statuses.Exists(status => status.IsActive));
            Assert.IsFalse(statuses[^1].IsActive);
            Assert.IsTrue(inputsReadyAtHandoff);
            Assert.IsNotNull(capturedPaths);
            Assert.AreEqual(1, capturedPaths!.Count);
            Assert.IsTrue(((IList<string>)capturedPaths).IsReadOnly);
            Assert.IsTrue(File.Exists(capturedPaths[0]), "受理後の入力は導入側の寿命へ移します。");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>二取得物を変更不能なpathsでまとめてsinkへ渡し、tree展開→結果表示の順に終え、受理後のstagingを保持します。進捗・結果・診断故障を重ねても引渡しを保ち、引渡しの分割と表示順序・回収の欠落を検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BulkDownloadedPackages_UsesCopiedInstallSnapshotBeforeTreeExpansionAndSummary(bool failNotifications)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new FakePlaylistUrlDownloadGateway(temporaryDirectory, new byte[] { 4, 5, 6 });
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ =>
            {
                if (failNotifications) { throw new IOException("download diagnostic unavailable"); }
            });
            var events = new List<string>();
            IReadOnlyList<string>? capturedPaths = null;
            bool inputsReadyAtHandoff = false;
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                MessageObserver = _ =>
                {
                    events.Add("summary");
                    if (failNotifications) { throw new IOException("summary unavailable"); }
                }
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot(),
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                    inputsReadyAtHandoff = paths.All(File.Exists);
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

            await workspace.RunPlaylistUrlBatchAsync(
                [
                    new Uri("https://example.invalid/first.zip"),
                    new Uri("https://example.invalid/second.zip")
                ],
                isDiffUrl: false);

            CollectionAssert.AreEqual(new[] { "sink", "expanded", "summary" }, events);
            Assert.AreEqual(1, dialogs.Confirmations.Count);
            Assert.AreEqual(1, dialogs.Messages.Count);
            Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
            Assert.IsTrue(statuses.Exists(status => status.IsActive));
            Assert.IsFalse(statuses[^1].IsActive);
            Assert.IsTrue(inputsReadyAtHandoff);
            Assert.IsNotNull(capturedPaths);
            Assert.AreEqual(2, capturedPaths!.Count);
            Assert.IsTrue(((IList<string>)capturedPaths).IsReadOnly);
            foreach (string path in capturedPaths)
            {
                Assert.IsTrue(File.Exists(path), "受理後の入力は導入側の寿命へ移します。");
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









    /// <summary>二件目の保存後・応答解放中に取消しても実終端を待ち、完了した二取得物を一回で渡して保持します。三件目の開始、完了済み入力の喪失と早すぎる引渡しを検出します。</summary>
    [DataTestMethod]
    [DataRow("api")]
    [DataRow("url")]
    public async Task DownloadedPackages_CancellationDuringResponseDisposalHandsOffCompletedDownloadsOnce(string ingress)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acquisition = null;
        string[] md5s = ["11111111111111111111111111111111", "22222222222222222222222222222222", "33333333333333333333333333333333"];
        Uri[] uris = [new("https://example.invalid/first.zip"), new("https://example.invalid/second.zip"), new("https://example.invalid/third.zip")];
        byte[] firstBytes = [1, 2, 3];
        byte[] secondBytes = [4, 5, 6];
        var firstStream = new DisposalGatedDownloadStream(firstBytes);
        var secondStream = new DisposalGatedDownloadStream(secondBytes, disposeEntered, disposeRelease.Task);
        try
        {
            var provider = new BatchHandoffLookupProvider(md5s.Zip(uris).ToDictionary(pair => pair.First, pair => pair.Second));
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(uris[0], () => new AppHttpResponse(uris[0], firstStream));
            gateway.AddResponse(uris[1], () => new AppHttpResponse(uris[1], secondStream));
            gateway.AddResponse(uris[2], () => new AppHttpResponse(uris[2], new MemoryStream([7, 8, 9], writable: false)));
            int handoffs = 0;
            IReadOnlyList<string>? capturedPaths = null;
            byte[][]? capturedBytes = null;
            bool responsesDisposedAtHandoff = false;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
    [DataTestMethod]
    // 単独列、本体一括、差分一括、外部API検索で同じBusy引渡しを確認する。
    [DataRow("single")]
    [DataRow("url")]
    [DataRow("diff")]
    [DataRow("api")]
    public async Task DownloadedPackages_BusyHandoffWarnsWithoutFallbackOrReplay(string ingress)
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var gate = new ChartFileOperationSynchronizer();
        IDisposable? competingOperation = null;
        PackageInstallWorkflowOwner? installOwner = null;
        try
        {
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadedInput_HoldsStagingUntilActualInstallTerminalWhilePlaylistIsBusy(bool failAfterStart)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
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
            var library = new TestBmsLibrary(db, null, null, string.Empty);
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadedInput_CancellationBeforeStartReclaimsOwnedSourceBeforeIdle(bool retireGeneration)
    {
        string root = Path.Combine(Path.GetTempPath(), "url-before-start-" + Guid.NewGuid().ToString("N"));
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
            var library = new TestBmsLibrary(db, null, null, string.Empty);
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: owner.TryEnqueue, installCompletion: owner.WaitForIdleAsync);
            acquisition = Task.Run(() => workspace.RunSinglePlaylistUrlAsync(uri));
            await TestUiDispatcherHost.AwaitNotificationAsync(statusEntered.Task, acquisition, "URL queue insertion");
            if (retireGeneration) { owner.AttachLibrary(new TestBmsLibrary(db, null, null, string.Empty)); }
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadedInput_UnacceptedWaitsForCleanupAndPreservesBothFailures(bool failHandoffAndCleanup)
    {
        string root = Path.Combine(Path.GetTempPath(), "url-rejected-" + Guid.NewGuid().ToString("N"));
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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
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

    /// <summary>単体・一括・APIの実取得と実導入・推定を通し、保留の正本、DB行、bytesを取得終端後も保ち、後の明示導入を成立させます。</summary>
    /// <remarks>HTTPとAPI候補だけを固定します。空TITLEによる実推定の保留を必須とし、推定結果を注入しません。管理sessionの初期化を共有するため非並列です。</remarks>
    [DataTestMethod]
    [DataRow("single")]
    [DataRow("url")]
    [DataRow("api")]
    [DoNotParallelize]
    public async Task DownloadedChart_RealEstimationRetainsPendingBytesForLaterManualInstall(string ingress)
    {
        await using var fixture = new PackageInstallWorkflowOwnerTests.AutomaticContinuationFixture(true, false);
        await fixture.InitializeAsync();
        int estimationApplied = 0;
        fixture.Observer.Applied = () => Interlocked.Increment(ref estimationApplied);
        string staging = TempDirectoryPublisher.Get("url-pending");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(staging);
            Uri[] uris = [new("https://example.invalid/first.bms"), new("https://example.invalid/second.bms")];
            byte[][] contents = [.. Enumerable.Range(0, 2).Select(index => Encoding.ASCII.GetBytes(
                "#TITLE\n#ARTIST\n#BPM 120\n#PLAYLEVEL 1\n#WAV01 sound.wav\n#00111:01\n#GENRE distinct-" + index + "\n"))];
            for (int index = 0; index < uris.Length; index++)
            {
                int captured = index;
                gateway.AddResponse(uris[index], () => new AppHttpResponse(uris[captured], new MemoryStream(contents[captured], writable: false)));
            }
            string[] md5s = ["11111111111111111111111111111111", "22222222222222222222222222222222"];
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: fixture.Owner.TryEnqueue,
                externalLookupService: new PlaylistExternalPackageLookupService([new BatchHandoffLookupProvider(md5s.Zip(uris).ToDictionary(pair => pair.First, pair => pair.Second))]),
                dialogService: new RecordingPlaylistUrlDialogService(),
                installCompletion: fixture.Owner.WaitForIdleAsync);
            if (ingress == "single") { await workspace.RunSinglePlaylistUrlAsync(uris[0]); }
            else if (ingress == "url") { await workspace.RunPlaylistUrlActionAsync(uris.Select(uri => CreatePlaylistUrlRow(uri.AbsoluteUri, null)), false); }
            else { await workspace.RunPlaylistExternalPackageLookupAsync(md5s.Select(CreatePlaylistExternalPackageRow)); }
            await fixture.Owner.WaitForIdleAsync();

            Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
            int expectedCount = ingress == "single" ? 1 : 2;
            ChartPackage[] pending = [.. fixture.Library.ChartPackagesPending];
            Assert.AreEqual(1, pending.Length, "同じ取得先の単体譜面は一つのパッケージとして保留します。");
            Assert.AreEqual(expectedCount, pending.Sum(package => package.ChartEntries.Count));
            Assert.IsTrue(estimationApplied > 0, "実推定の結果適用を通過して保留へ残ることを必須とします。");
            Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
                Assert.IsTrue(string.Equals(pending.Single().path, db.Table<LR2SongDBExtended.install>().Single().path, StringComparison.OrdinalIgnoreCase));
            }
            foreach (PackageChartEntry entry in pending.SelectMany(package => package.ChartEntries))
            {
                Assert.IsTrue(string.IsNullOrEmpty(entry.Chart.InstallDestination));
                string source = entry.Chart.Path;
                int index = string.Equals(Path.GetFileName(source), "first.bms", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                Assert.IsTrue(File.Exists(source), "取得Taskとowner実idleの後も保留実体を保持します。");
                CollectionAssert.AreEqual(contents[index], File.ReadAllBytes(source));
                entry.ApplyInstallDestinationMetadata(fixture.Destination, "Song", "");
            }
            PendingInstallBatchResult result = fixture.Library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(pending);
            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
            Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
            {
                foreach (Uri uri in uris.Take(expectedCount))
                {
                    string destination = Path.Combine(fixture.Destination, Path.GetFileName(uri.AbsolutePath));
                    Assert.IsTrue(File.Exists(destination));
                    Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
                    Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => string.Equals(chart.Path, destination, StringComparison.OrdinalIgnoreCase)));
                    int index = Array.IndexOf(uris, uri);
                    CollectionAssert.AreEqual(contents[index], File.ReadAllBytes(destination));
                }
            }
        }
        finally
        {
            await fixture.Owner.WaitForIdleAsync();
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
    }

    /// <summary>取得先を固定し、外部API検索ownerの実引渡し経路を通すprovider。</summary>
    private sealed class HandoffLookupProvider(Uri downloadUri) : IPlaylistExternalPackageLookupProvider
    {
        public string ProviderId => "handoff-test";

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
            => Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUri));
    }

    private sealed class BatchHandoffLookupProvider(IReadOnlyDictionary<string, Uri> downloadUris) : IPlaylistExternalPackageLookupProvider
    {
        public string ProviderId => "batch-handoff-test";

        internal List<string> RequestedMd5s { get; } = [];

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
        {
            RequestedMd5s.Add(chartMd5);
            return Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUris[chartMd5]));
        }
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

    private PlaylistWorkspaceViewModel CreateWorkspace(
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
            PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore();
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
            acquisitionWorkflow ?? PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            externalLookupService ?? PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            optionsProvider ?? PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            ownedInstallSink ?? (installSink == null ? PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink : request => installSink(Array.AsReadOnly(request.Paths))),
            browserSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            () => ownedPlaylistStore.Store,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, urlPresentationScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, dialogService, playlistUrlInstallCompletionProvider: installCompletion);
        workspace.PlaylistUrlInstallTreeExpansionRequested += actualTreeExpansionSink;
        return workspace;
    }



    private static PlaylistDetailRow CreatePlaylistUrlRow(string? url, string? diffUrl)
        => new PlaylistDetailSourceRow(CreatePlaylistEntry(string.Empty, url, diffUrl), resolvedChart: null).CreateViewRow();

    private static PlaylistDetailRow CreatePlaylistExternalPackageRow(string md5)
        => new PlaylistDetailSourceRow(CreatePlaylistEntry(md5), resolvedChart: null).CreateViewRow();

    private static BMSTableEntry CreatePlaylistEntry(string md5, string? url = null, string? diffUrl = null)
    {
        return BMSTableEntry.CreateHydratedPlaylistEntry(
            playlistId: 1, md5Value: md5, sha256Value: null, levelValue: null,
            titleValue: "Playlist Entry", artistValue: "Artist", folderValue: string.Empty,
            lr2BmsIdValue: string.Empty, urlValue: url ?? string.Empty, urlDiffValue: diffUrl ?? string.Empty,
            nameDiffValue: string.Empty, orgMd5Value: string.Empty, addDateValue: null,
            commentValue: string.Empty, memoValue: string.Empty, isRemovedValue: false);
    }

    private static AppHttpResponse CreateHttpResponse(
        Uri requestUri,
        byte[] content,
        string contentType,
        string? contentDisposition = null)
    {
        var responseMessage = new HttpResponseMessage
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
            Content = new ByteArrayContent(content)
        };
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

    private sealed class RecordingPlaylistUrlDialogService : IUiDialogService
    {
        internal List<UiConfirmationRequest> Confirmations { get; } = [];

        internal List<UiMessageRequest> Messages { get; } = [];

        internal TaskCompletionSource<UiDialogResult>? PendingConfirmation { get; set; }

        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal Action<UiMessageRequest>? MessageObserver { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            MessageObserver?.Invoke(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            Confirmations.Add(request);
            return PendingConfirmation?.Task
                ?? Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingPlaylistUrlDownloadGateway : IPlaylistUrlDownloadGateway
    {
        private readonly string temporaryDirectory;

        private readonly Dictionary<string, Func<AppHttpResponse>> responses =
            new(StringComparer.OrdinalIgnoreCase);

        internal RecordingPlaylistUrlDownloadGateway(string temporaryDirectory)
        {
            this.temporaryDirectory = temporaryDirectory;
        }

        internal List<Uri> RequestedUris { get; } = [];

        internal int OpenWriteCount { get; private set; }

        internal int GetTemporaryDirectoryCount { get; private set; }

        internal Exception? TransportException { get; set; }

        internal Exception? DeleteFailure { get; set; }

        internal int DeleteCount { get; private set; }

        internal Action? BeforeDelete { get; set; }

        internal void AddResponse(Uri uri, Func<AppHttpResponse> responseFactory)
        {
            responses[uri.AbsoluteUri] = responseFactory;
        }

        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUris.Add(uri);
            if (TransportException is not null)
            {
                throw TransportException;
            }
            if (!responses.TryGetValue(uri.AbsoluteUri, out Func<AppHttpResponse>? responseFactory))
            {
                throw new InvalidOperationException("No fake response was configured for " + uri);
            }
            return Task.FromResult(responseFactory());
        }

        public string GetTemporaryDirectory()
        {
            GetTemporaryDirectoryCount++;
            return temporaryDirectory;
        }

        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share)
        {
            OpenWriteCount++;
            return new FileStream(path, mode, access, share);
        }

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path)
        {
            DeleteCount++;
            BeforeDelete?.Invoke();
            if (DeleteFailure != null) { throw DeleteFailure; }
            File.Delete(path);
        }
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
