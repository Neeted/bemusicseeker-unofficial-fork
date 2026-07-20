using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlaylistUrlAcquisitionOwnershipTests
{
    [TestMethod]
    public void OptionsSnapshot_CapturesAutoInstallSettingsWithoutExposingSettingsObject()
    {
        bool previousScan = Settings.Default.ScanBmsFilesOnStartup;
        bool previousAutoInstall = Settings.Default.AutoInstall;
        try
        {
            Settings.Default.ScanBmsFilesOnStartup = true;
            Settings.Default.AutoInstall = true;

            PlaylistUrlAcquisitionOptionsSnapshot snapshot =
                PlaylistUrlAcquisitionOptionsSnapshot.CreateCurrent(Settings.Default);

            Assert.IsTrue(snapshot.ScanBmsFilesOnStartup);
            Assert.IsTrue(snapshot.AutoInstall);
            Assert.IsTrue(snapshot.ShouldAutoInstall);
        }
        finally
        {
            Settings.Default.ScanBmsFilesOnStartup = previousScan;
            Settings.Default.AutoInstall = previousAutoInstall;
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

        await workspace.OpenSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/"));

        Assert.AreEqual("https://example.invalid/folder/", openedUri?.ToString());
        Assert.AreEqual(1, dispatchCount);
    }

    [TestMethod]
    public async Task BrowserFallbackSinkExceptionIsNotSuppressed()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(
            action => action(),
            browserSink: _ => throw new InvalidOperationException("browser sink failure"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.OpenSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

    [TestMethod]
    public async Task BulkBrowserFallbackPublishesSummaryAndReturnsToInactiveState()
    {
        int dispatchCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action =>
        {
            dispatchCount++;
            action();
        }, () => new PlaylistUrlAcquisitionOptionsSnapshot());
        workspace.PlaylistUrlAcquisitionConfirmationRequested += (_, request) => request.Confirmed = true;
        PlaylistUrlAcquisitionSummaryReadyEventArgs? summary = null;
        List<PlaylistUrlDownloadStatusSnapshot> statuses = [];
        workspace.PlaylistUrlAcquisitionSummaryReady += (_, value) => summary = value;
        workspace.PlaylistUrlDownloadStatusChanged += (_, value) => statuses.Add(value);

        await workspace.DownloadSelectedPlaylistUrlsAsync(
            [new Uri("https://example.invalid/folder/")],
            isDiffUrl: false);

        Assert.IsNotNull(summary);
        Assert.AreEqual(1, summary!.TargetCount);
        Assert.AreEqual(1, summary.BrowserFallbackCount);
        Assert.AreEqual(0, summary.DownloadedCount);
        Assert.IsFalse(workspace.IsPlaylistUrlDownloadRunning);
        Assert.IsTrue(statuses.Count >= 2);
        Assert.IsFalse(statuses[statuses.Count - 1].IsActive);
        Assert.AreEqual(1, dispatchCount);
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
                },
                treeExpansionSink: () => events.Add("expanded"));

            await workspace.OpenSinglePlaylistUrlAsync(new Uri("https://example.invalid/single.zip"));

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
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(
                action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot(),
                acquisitionWorkflow,
                paths =>
                {
                    events.Add("sink");
                    capturedPaths = paths;
                },
                treeExpansionSink: () => events.Add("expanded"));
            workspace.PlaylistUrlAcquisitionConfirmationRequested += (_, request) => request.Confirmed = true;
            workspace.PlaylistUrlAcquisitionSummaryReady += (_, _) => events.Add("summary");

            await workspace.DownloadSelectedPlaylistUrlsAsync(
                [
                    new Uri("https://example.invalid/first.zip"),
                    new Uri("https://example.invalid/second.zip")
                ],
                isDiffUrl: false);

            CollectionAssert.AreEqual(new[] { "sink", "expanded", "summary" }, events);
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
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink,
                PlaylistWorkspaceTestPorts.PlaylistSummarySelectionRestoreSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, request => request(false), request => request(false), () => false, _ => false, (_, _) => false, (_, _) => false));

        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspace(
            action => action(),
            browserSink: null,
            useDefaultBrowserSink: false));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspace(
            action => action(),
            treeExpansionSink: null,
            useDefaultTreeExpansionSink: false));
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
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            null!,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            null!,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistSummaryColumnSettingsStore()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(
            null!,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            null!));
    }

    private static PlaylistWorkspaceViewModel CreateWorkspace(
        Action<Action> dispatch,
        Func<PlaylistUrlAcquisitionOptionsSnapshot>? optionsProvider = null,
        PlaylistUrlAcquisitionWorkflow? acquisitionWorkflow = null,
        Action<IReadOnlyList<string>>? installSink = null,
        Action<Uri>? browserSink = null,
        bool useDefaultBrowserSink = true,
        Action? treeExpansionSink = null,
        bool useDefaultTreeExpansionSink = true)
    {
        return new PlaylistWorkspaceViewModel(
            dispatch,
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            acquisitionWorkflow ?? PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            optionsProvider ?? PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            installSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            useDefaultBrowserSink
                ? browserSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink
                : browserSink!,
            useDefaultTreeExpansionSink
                ? treeExpansionSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink
                : treeExpansionSink!,
            PlaylistWorkspaceTestPorts.PlaylistSummarySelectionRestoreSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, request => request(false), request => request(false), () => false, _ => false, (_, _) => false, (_, _) => false);
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceWithLoggingPorts(
        Action<Exception, string> externalWarningLog,
        Action<string> externalInfoLog,
        Action<Exception, string> beatorajaWarningLog,
        Action<string> beatorajaInfoLog,
        IMainChartColumnSettingsStore columnSettingsStore,
        PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
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
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink,
                PlaylistWorkspaceTestPorts.PlaylistSummarySelectionRestoreSink,
            externalWarningLog,
            externalInfoLog,
            beatorajaWarningLog,
            beatorajaInfoLog,
            columnSettingsStore,
            playlistSummaryBmtSort,
            keywordSearchHistorySettingsStore,
            playlistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, request => request(false), request => request(false), () => false, _ => false, (_, _) => false, (_, _) => false);
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceWithColumnStore(
        IMainChartColumnSettingsStore columnSettingsStore,
        PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
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
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink,
                PlaylistWorkspaceTestPorts.PlaylistSummarySelectionRestoreSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            columnSettingsStore,
            playlistSummaryBmtSort,
            keywordSearchHistorySettingsStore,
            playlistStoreProvider,
            propertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, request => request(false), request => request(false), () => false, _ => false, (_, _) => false, (_, _) => false);
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
}
