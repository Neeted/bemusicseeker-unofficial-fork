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
        });
        workspace.ConfigurePlaylistUrlAcquisitionInstallQueue(() => false);
        workspace.ConfigurePlaylistUrlAcquisitionOptions(() => new PlaylistUrlAcquisitionOptionsSnapshot
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
        });
        workspace.ConfigurePlaylistUrlAcquisitionInstallQueue(() => false);
        workspace.ConfigurePlaylistUrlAcquisitionOptions(() => new PlaylistUrlAcquisitionOptionsSnapshot());
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
    public async Task WorkspaceWithoutConfiguredOptionsFailsExplicitly()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspace(action => action());
        workspace.ConfigurePlaylistUrlAcquisitionInstallQueue(() => false);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.OpenSinglePlaylistUrlAsync(new Uri("https://example.invalid/folder/")));
    }

    private static PlaylistWorkspaceViewModel CreateWorkspace(Action<Action> dispatch)
    {
        return new PlaylistWorkspaceViewModel(
            dispatch,
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
    }
}
