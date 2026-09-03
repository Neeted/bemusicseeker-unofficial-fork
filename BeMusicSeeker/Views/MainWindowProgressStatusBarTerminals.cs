using System;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Narrows status-bar action events to the existing workflow owners.
/// </summary>
internal sealed class MainWindowProgressStatusBarTerminals
{
    private readonly Action cancelInstallPipeline;

    private readonly Action cancelMaintenanceRescan;

    private readonly Action retryLr2Sync;

    /// <summary>
    /// Initializes the status-bar action terminal bundle.
    /// </summary>
    /// <param name="cancelInstallPipeline">
    /// Cancels the currently prioritized playlist URL download or package-install pipeline.
    /// </param>
    /// <param name="cancelMaintenanceRescan">Cancels the owned maintenance rescan.</param>
    /// <param name="retryLr2Sync">Requests the owned LR2 sync retry.</param>
    internal MainWindowProgressStatusBarTerminals(
        Action cancelInstallPipeline,
        Action cancelMaintenanceRescan,
        Action retryLr2Sync)
    {
        this.cancelInstallPipeline = cancelInstallPipeline
            ?? throw new ArgumentNullException(nameof(cancelInstallPipeline));
        this.cancelMaintenanceRescan = cancelMaintenanceRescan
            ?? throw new ArgumentNullException(nameof(cancelMaintenanceRescan));
        this.retryLr2Sync = retryLr2Sync
            ?? throw new ArgumentNullException(nameof(retryLr2Sync));
    }

    /// <summary>
    /// Cancels the active install pipeline through its existing owner priority rule.
    /// </summary>
    internal void CancelInstallPipeline() => cancelInstallPipeline();

    /// <summary>
    /// Cancels the active maintenance rescan through its existing owner.
    /// </summary>
    internal void CancelMaintenanceRescan() => cancelMaintenanceRescan();

    /// <summary>
    /// Requests an LR2 sync retry through its existing owner.
    /// </summary>
    internal void RetryLr2Sync() => retryLr2Sync();

    /// <summary>
    /// Creates the default terminal bundle without changing the existing workflow routes.
    /// </summary>
    /// <param name="viewModel">The shell view model owning all status-bar workflows.</param>
    /// <returns>A bundle delegating each action to its existing workflow owner.</returns>
    internal static MainWindowProgressStatusBarTerminals Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return CreateCore(
            () => viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning,
            viewModel.PlaylistWorkspace.CancelPlaylistUrlDownload,
            viewModel.PackageInstallWorkflow.CancelAll,
            viewModel.MaintenanceRescanWorkflow.Cancel,
            viewModel.Lr2SongDbSyncWorkflow.RequestStatusBarRetry);
    }

    /// <summary>
    /// Creates the narrow status-bar action bundle while preserving playlist-download priority.
    /// </summary>
    /// <param name="playlistUrlDownloadIsRunning">Reports whether playlist URL acquisition currently owns cancellation.</param>
    /// <param name="cancelPlaylistUrlDownload">Cancels the active playlist URL download.</param>
    /// <param name="cancelPackageInstall">Cancels the package-install pipeline when no playlist download is active.</param>
    /// <param name="cancelMaintenanceRescan">Cancels the owned maintenance rescan.</param>
    /// <param name="retryLr2Sync">Requests the owned LR2 sync retry.</param>
    /// <returns>A terminal bundle that delegates each action exactly once to the supplied owner action.</returns>
    internal static MainWindowProgressStatusBarTerminals CreateCore(
        Func<bool> playlistUrlDownloadIsRunning,
        Action cancelPlaylistUrlDownload,
        Action cancelPackageInstall,
        Action cancelMaintenanceRescan,
        Action retryLr2Sync)
    {
        ArgumentNullException.ThrowIfNull(playlistUrlDownloadIsRunning);
        ArgumentNullException.ThrowIfNull(cancelPlaylistUrlDownload);
        ArgumentNullException.ThrowIfNull(cancelPackageInstall);
        ArgumentNullException.ThrowIfNull(cancelMaintenanceRescan);
        ArgumentNullException.ThrowIfNull(retryLr2Sync);

        return new MainWindowProgressStatusBarTerminals(
            () =>
            {
                if (playlistUrlDownloadIsRunning())
                {
                    cancelPlaylistUrlDownload();
                    return;
                }

                cancelPackageInstall();
            },
            cancelMaintenanceRescan,
            retryLr2Sync);
    }
}
