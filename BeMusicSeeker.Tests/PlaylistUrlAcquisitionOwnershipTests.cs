using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action =>
        {
            dispatchCount++;
            action();
        }, () => new PlaylistUrlAcquisitionOptionsSnapshot
        {
            ScanBmsFilesOnStartup = false,
            AutoInstall = true
        });
        Uri? openedUri = null;
        workspace.PlaylistUrlBrowserOpenRequested += (_, request) => openedUri = request.Uri;

        await workspace.OpenSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/"));

        Assert.AreEqual("https://example.invalid/folder/", openedUri?.ToString());
        Assert.AreEqual(1, dispatchCount);
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
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitExternalPlaylistImportLoggingPorts()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            null!,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            null!,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitBeatorajaTableUrlImportLoggingPorts()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            null!,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore));
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithLoggingPorts(
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            null!,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore));
    }

    [TestMethod]
    public void ConstructorRequiresExplicitPlaylistSummaryColumnSettingsStore()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CreateWorkspaceWithColumnStore(null!));
    }

    private static PlaylistWorkspaceViewModel CreateWorkspace(
        Action<Action> dispatch,
        Func<PlaylistUrlAcquisitionOptionsSnapshot>? optionsProvider = null)
    {
        return new PlaylistWorkspaceViewModel(
            dispatch,
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            optionsProvider ?? PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore);
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceWithLoggingPorts(
        Action<Exception, string> externalWarningLog,
        Action<string> externalInfoLog,
        Action<Exception, string> beatorajaWarningLog,
        Action<string> beatorajaInfoLog,
        IMainChartColumnSettingsStore columnSettingsStore)
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
            externalWarningLog,
            externalInfoLog,
            beatorajaWarningLog,
            beatorajaInfoLog,
            columnSettingsStore);
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceWithColumnStore(
        IMainChartColumnSettingsStore columnSettingsStore)
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
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            columnSettingsStore);
    }
}
