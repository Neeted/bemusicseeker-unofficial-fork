using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistUrlAcquisitionOwnershipTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();

    private readonly List<PlaylistWorkspaceTestPorts.OwnedPlaylistStore> ownedPlaylistStores = [];

    [TestCleanup]
    public void CleanupOwnedPlaylistStores()
    {
        for (int index = ownedPlaylistStores.Count - 1; index >= 0; index--)
        {
            ownedPlaylistStores[index].Dispose();
        }
        ownedPlaylistStores.Clear();
    }
    [TestMethod]
    public void OptionsSnapshot_CapturesAutoInstallSettingsWithoutExposingSettingsObject()
    {
        bool previousScan = testSettings.ScanBmsFilesOnStartup;
        bool previousAutoInstall = testSettings.AutoInstall;
        try
        {
            testSettings.ScanBmsFilesOnStartup = true;
            testSettings.AutoInstall = true;

            PlaylistUrlAcquisitionOptionsSnapshot snapshot =
                PlaylistUrlAcquisitionOptionsSnapshot.CreateCurrent(testSettings);

            Assert.IsTrue(snapshot.ScanBmsFilesOnStartup);
            Assert.IsTrue(snapshot.AutoInstall);
            Assert.IsTrue(snapshot.ShouldAutoInstall);
        }
        finally
        {
            testSettings.ScanBmsFilesOnStartup = previousScan;
            testSettings.AutoInstall = previousAutoInstall;
        }
    }

    [TestMethod]
    public async Task DownloadCandidate_SupportedChartAndArchiveNamesWriteExactBasenameExtensionAndBytes()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        byte[] expectedBytes = [0x10, 0x20, 0x30, 0x40];
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

    [DataTestMethod]
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

    [TestMethod]
    public async Task DownloadCandidate_UnsupportedRecursiveSchemeFailsForKnownHtmlAndJsonSourceForms()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Uri responseRequestUri = new("https://example.invalid/download/package.zip");
        Uri responseUri = new("file:///C:/secret.zip");
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
            gateway.AddResponse(responseRequestUri, () => new AppHttpResponse(
                responseUri,
                new MemoryStream([0x41, 0x42, 0x43], writable: false)));
            foreach ((Uri pageUri, string html) in cases)
            {
                gateway.AddResponse(pageUri, () => CreateHttpResponse(
                    pageUri,
                    Encoding.UTF8.GetBytes(html),
                    "text/html"));
            }
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });

            PlaylistUrlDownloadResult responseResult = await workflow.DownloadCandidateAsync(responseRequestUri);

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, responseResult.Kind);
            Assert.IsTrue(responseResult.IsUnsupportedScheme);
            CollectionAssert.AreEqual(
                new[] { responseRequestUri.AbsoluteUri },
                gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            Assert.AreEqual(1, gateway.GetTemporaryDirectoryCount);
            Assert.AreEqual(0, gateway.OpenWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory).Length);

            foreach ((Uri pageUri, string _) in cases)
            {
                PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(pageUri);

                Assert.AreEqual(PlaylistUrlDownloadResultKind.Failed, result.Kind, pageUri.ToString());
                Assert.IsTrue(result.IsUnsupportedScheme, pageUri.ToString());
            }

            CollectionAssert.AreEqual(
                cases.Select(testCase => testCase.PageUri.AbsoluteUri).ToArray(),
                gateway.RequestedUris.Skip(1).Select(uri => uri.AbsoluteUri).ToArray());
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

    [TestMethod]
    public async Task DownloadCandidate_ExplicitCancellationPropagatesOperationCanceledException()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
            var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            using var cancellation = new CancellationTokenSource();
            Task<PlaylistUrlDownloadResult> acquisition = workflow.DownloadCandidateAsync(
                new Uri("https://example.invalid/download/running.zip"),
                cancellationToken: cancellation.Token);

            await gateway.ReadStarted.Task;
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
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task WorkspaceRequiresExplicitCompositionPortsAndForwardsBrowserFallbackOnDispatcher()
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

    [TestMethod]
    public async Task BrowserFallbackSinkExceptionIsNotSuppressed()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
            action => action(),
            browserSink: _ => throw new InvalidOperationException("browser sink failure"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

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

        Assert.IsFalse(acquisition.IsCompleted);
        Assert.AreEqual(1, dialogs.Confirmations.Count);

        dialogs.PendingConfirmation.SetResult(
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        await acquisition;

        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
    }

    [TestMethod]
    public async Task PlaylistUrlCommandGateBlocksConcurrentRouteDuringConfirmation()
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

        await workspace.RunSinglePlaylistUrlAsync(
            new Uri("https://example.invalid/second-during-confirmation/"));

        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual(0, browserOpenCount);

        dialogs.PendingConfirmation.SetResult(
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        await first;
    }

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

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/summary-failure/")],
                isDiffUrl: false));

        dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await workspace.RunPlaylistUrlBatchAsync(
            [new Uri("https://example.invalid/summary-retry/")],
            isDiffUrl: false);

        Assert.AreEqual(2, dialogs.Confirmations.Count);
        Assert.AreEqual(2, dialogs.Messages.Count);
    }

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

    [TestMethod]
    public void PlaylistUrlContextMenuAvailability_DisablesExternalLookupWhileInstallQueueIsActive()
    {
        PlaylistDetailRow externalRow = CreatePlaylistExternalPackageRow(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        PlaylistWorkspaceViewModel availableWorkspace = CreateWorkspace(action => action());
        PlaylistWorkspaceViewModel blockedWorkspace = CreateWorkspace(
            action => action(),
            installQueueActiveProvider: () => true);

        Assert.IsTrue(
            availableWorkspace.CapturePlaylistUrlContextMenuAvailability(externalRow, [externalRow])
                .CanFindExternalPackage);
        Assert.IsFalse(
            blockedWorkspace.CapturePlaylistUrlContextMenuAvailability(externalRow, [externalRow])
                .CanFindExternalPackage);
    }

    [TestMethod]
    public async Task PlaylistUrlContextMenuAvailability_DoesNotTreatPendingConfirmationAsDownload()
    {
        var dialogs = new RecordingPlaylistUrlDialogService
        {
            PendingConfirmation = new TaskCompletionSource<UiDialogResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        int installQueueProviderCalls = 0;
        PlaylistDetailRow row = CreatePlaylistUrlRow(
            "https://example.invalid/pending/",
            "https://example.invalid/pending-diff/");
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
            action => action(),
            dialogService: dialogs,
            installQueueActiveProvider: () =>
            {
                installQueueProviderCalls++;
                return false;
            });

        Task acquisition = workspace.RunPlaylistUrlBatchAsync([new Uri("https://example.invalid/pending/")], isDiffUrl: false);
        Assert.IsFalse(acquisition.IsCompleted);

        PlaylistUrlContextMenuAvailability availability = workspace.CapturePlaylistUrlContextMenuAvailability(
            row,
            [row]);

        Assert.IsTrue(availability.CanOpenUrl);
        Assert.IsTrue(availability.CanOpenDiffUrl);
        Assert.IsFalse(availability.CanFindExternalPackage);
        Assert.AreEqual(2, installQueueProviderCalls);

        dialogs.PendingConfirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
        await acquisition;
    }

    [TestMethod]
    public async Task PlaylistUrlContextMenuAvailability_ShortCircuitsInstallQueueProviderWhileDownloading()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            int installQueueProviderCalls = 0;
            PlaylistDetailRow row = CreatePlaylistUrlRow(
                "https://example.invalid/running.zip",
                "https://example.invalid/running-diff.zip");
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                acquisitionWorkflow: acquisitionWorkflow,
                dialogService: dialogs,
                installQueueActiveProvider: () =>
                {
                    installQueueProviderCalls++;
                    return false;
                });

            Task acquisition = workspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/running.zip")],
                isDiffUrl: false);
            await gateway.ReadStarted.Task;
            int callsBeforeAvailability = installQueueProviderCalls;

            PlaylistUrlContextMenuAvailability availability = workspace.CapturePlaylistUrlContextMenuAvailability(
                row,
                [row]);

            Assert.IsFalse(availability.CanOpenUrl);
            Assert.IsFalse(availability.CanOpenDiffUrl);
            Assert.IsFalse(availability.CanFindExternalPackage);
            Assert.AreEqual(callsBeforeAvailability, installQueueProviderCalls);

            gateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/running.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            await acquisition;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistUrlProgressAttachedHubPublishesActiveCancelAndTerminalStates()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlAcquisitionOwnershipTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new BlockingPlaylistUrlDownloadGateway(temporaryDirectory);
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

            Task acquisition = workspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/running.zip")],
                isDiffUrl: false);
            await gateway.ReadStarted.Task;

            Assert.IsTrue(hub.IsInstallPipelineStatusActive);
            Assert.IsTrue(hub.InstallPipelineCanCancel);
            workspace.CancelPlaylistUrlDownload();
            Assert.IsFalse(hub.InstallPipelineCanCancel);

            gateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/running.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            await acquisition;

            Assert.IsFalse(hub.IsInstallPipelineStatusActive);
            Assert.AreEqual(0, hub.InstallPipelineValue);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SingleDownloadedPackage_UsesInstallSinkBeforeTreeExpansionPresentation()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new FakePlaylistUrlDownloadGateway(temporaryDirectory, new byte[] { 1, 2, 3 });
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var events = new List<string>();
            IReadOnlyList<string>? capturedPaths = null;
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                    return true;
                },
                treeExpansionSink: () => events.Add("expanded"));

            await workspace.RunSinglePlaylistUrlAsync(new Uri("https://example.invalid/single.zip"));

            CollectionAssert.AreEqual(new[] { "sink", "expanded" }, events);
            Assert.IsNotNull(capturedPaths);
            Assert.AreEqual(1, capturedPaths!.Count);
            Assert.IsTrue(((IList<string>)capturedPaths).IsReadOnly);
            Assert.IsTrue(File.Exists(capturedPaths[0]));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task BulkDownloadedPackages_UsesCopiedInstallSnapshotBeforeTreeExpansionAndSummary()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var gateway = new FakePlaylistUrlDownloadGateway(temporaryDirectory, new byte[] { 4, 5, 6 });
            var acquisitionWorkflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
            var events = new List<string>();
            IReadOnlyList<string>? capturedPaths = null;
            var dialogs = new RecordingPlaylistUrlDialogService
            {
                MessageObserver = _ => events.Add("summary")
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot(),
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                    return true;
                },
                treeExpansionSink: () => events.Add("expanded"),
                dialogService: dialogs);

            await workspace.RunPlaylistUrlBatchAsync(
                [
                    new Uri("https://example.invalid/first.zip"),
                    new Uri("https://example.invalid/second.zip")
                ],
                isDiffUrl: false);

            CollectionAssert.AreEqual(new[] { "sink", "expanded", "summary" }, events);
            Assert.AreEqual(1, dialogs.Confirmations.Count);
            Assert.AreEqual(1, dialogs.Messages.Count);
            Assert.IsNotNull(capturedPaths);
            Assert.AreEqual(2, capturedPaths!.Count);
            Assert.IsTrue(((IList<string>)capturedPaths).IsReadOnly);
            foreach (string path in capturedPaths)
            {
                Assert.IsTrue(File.Exists(path));
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

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistUrlPorts()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            null,
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));

        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspace(
            action => action(),
            browserSink: null,
            useDefaultBrowserSink: false));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitExternalPlaylistImportLoggingPorts()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            null!,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            null!,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitBeatorajaTableUrlImportLoggingPorts()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            null!,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            null!,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistSummaryColumnSettingsStore()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(
            null!,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistSummaryBmtSortCoordinator()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            null!,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitKeywordSearchHistorySettingsStore()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            null!,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistStoreProvider()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            null!,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistPropertySaveService()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            null!));
    }

    /// <summary>
    /// URL-HANDOFF: 単体列・本体/差分取り込み・外部 API の取得成功後に共通受付が Busy の場合、
    /// 警告で終わり、導入・tree 展開・ブラウザ fallback・後続の自動再実行を行いません。
    /// 通信段階ではライブラリ変更受付を占有しない契約を、実 queue への引渡しで検証します。
    /// </summary>
    [DataTestMethod]
    [DataRow("single")]
    [DataRow("url")]
    [DataRow("diff")]
    [DataRow("api")]
    public async Task DownloadedPackages_BusyHandoffWarnsWithoutFallbackOrReplay(string ingress)
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    installCount++;
                    return [];
                }), action => { action(); return true; });
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
            var workspace = CreateWorkspace(
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                paths =>
                {
                    handoffCount++;
                    downloaded = paths;
                    return installOwner.Enqueue(paths);
                },
                browserSink: _ => browserCount++,
                treeExpansionSink: () => expansionCount++,
                dialogService: dialogs,
                installQueueActiveProvider: () => installOwner.IsActive,
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
            Assert.IsTrue(downloaded.All(File.Exists), "未受理を理由に取得済み source を削除しない。");
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
                await installOwner.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>取得先だけを固定し、外部 API 検索 owner の実引渡し経路を通す provider。</summary>
    private sealed class HandoffLookupProvider(Uri downloadUri) : IPlaylistExternalPackageLookupProvider
    {
        public string ProviderId => "handoff-test";

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
            => Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUri));
    }

    private PlaylistWorkspaceViewModel CreateWorkspace(
        Action<Action> dispatch,
        Func<PlaylistUrlAcquisitionOptionsSnapshot>? optionsProvider = null,
        PlaylistUrlAcquisitionWorkflow? acquisitionWorkflow = null,
        Func<IReadOnlyList<string>, bool>? installSink = null,
        Action<Uri>? browserSink = null,
        bool useDefaultBrowserSink = true,
        Action? treeExpansionSink = null,
        bool useDefaultTreeExpansionSink = true,
        IUiDialogService? dialogService = null,
        Func<Action, Task>? presentationScheduler = null,
        Func<bool>? installQueueActiveProvider = null,
        PlaylistExternalPackageLookupService? externalLookupService = null)
    {
        PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedPlaylistStore =
            PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore();
        ownedPlaylistStores.Add(ownedPlaylistStore);
        Func<Action, Task> urlPresentationScheduler = presentationScheduler
            ?? (action =>
            {
                dispatch(action);
                return Task.CompletedTask;
            });
        Action actualTreeExpansionSink = useDefaultTreeExpansionSink
            ? treeExpansionSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink
            : treeExpansionSink!;
        PlaylistWorkspaceViewModel workspace = new PlaylistWorkspaceViewModel(
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
            installQueueActiveProvider ?? PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            installSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            useDefaultBrowserSink
                ? browserSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink
                : browserSink!,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, urlPresentationScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, dialogService);
        workspace.PlaylistUrlInstallTreeExpansionRequested += actualTreeExpansionSink;
        return workspace;
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceWithLoggingPorts(
        Action<Exception, string> externalWarningLog,
        Action<string> externalInfoLog,
        Action<Exception, string> beatorajaWarningLog,
        Action<string> beatorajaInfoLog,
        IMainChartColumnSettingsStore columnSettingsStore,
        PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore,
        Func<BMSPlaylist> playlistStoreProvider)
    {
        return new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            externalWarningLog,
            externalInfoLog,
            beatorajaWarningLog,
            beatorajaInfoLog,
            columnSettingsStore,
            playlistSummaryBmtSort,
            keywordSearchHistorySettingsStore,
            keywordSearchFavoritesSettingsStore,
            playlistStoreProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceWithColumnStore(
        IMainChartColumnSettingsStore columnSettingsStore,
        PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore,
        Func<BMSPlaylist> playlistStoreProvider,
        PlaylistPropertySaveService propertySaveService)
    {
        return new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            columnSettingsStore,
            playlistSummaryBmtSort,
            keywordSearchHistorySettingsStore,
            keywordSearchFavoritesSettingsStore,
            playlistStoreProvider,
            propertySaveService,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
    }

    private static PlaylistDetailRow CreatePlaylistUrlRow(string? url, string? diffUrl)
    {
        var row = (PlaylistDetailRow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlaylistDetailRow));
        SetPrivateField(row, "url", string.IsNullOrWhiteSpace(url) ? null : new Uri(url));
        SetPrivateField(row, "urlDiff", string.IsNullOrWhiteSpace(diffUrl) ? null : new Uri(diffUrl));
        return row;
    }

    private static PlaylistDetailRow CreatePlaylistExternalPackageRow(string md5)
    {
        var row = (PlaylistDetailRow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlaylistDetailRow));
        SetPrivateField(row, "<Entry>k__BackingField", CreatePlaylistEntry(md5));
        SetPrivateField(row, "<IsOwned>k__BackingField", false);
        return row;
    }

    private static BMSTableEntry CreatePlaylistEntry(string md5)
    {
        return BMSTableEntry.CreateHydratedPlaylistEntry(
            playlistId: 1,
            md5Value: md5,
            sha256Value: null,
            levelValue: null,
            titleValue: "Playlist Entry",
            artistValue: "Artist",
            folderValue: string.Empty,
            lr2BmsIdValue: string.Empty,
            urlValue: string.Empty,
            urlDiffValue: string.Empty,
            nameDiffValue: string.Empty,
            orgMd5Value: string.Empty,
            addDateValue: null,
            commentValue: string.Empty,
            memoValue: string.Empty,
            isRemovedValue: false);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
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

        public void DeleteFile(string path) => File.Delete(path);
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
