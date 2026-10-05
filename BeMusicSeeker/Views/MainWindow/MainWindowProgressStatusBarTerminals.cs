using System;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// 表示行の操作を既存の管理主体へ直接接続します。
/// </summary>
internal sealed class MainWindowProgressStatusBarTerminals
{
    private readonly Action cancelInstallPipeline;

    private readonly Action cancelPlaylistUrlDownload;

    private readonly Action cancelMaintenanceRescan;

    private readonly Action retryLr2Sync;

    /// <summary>
    /// 行ごとの取消・再試行先を初期化します。
    /// </summary>
    /// <param name="cancelInstallPipeline">
    /// 導入行が所有するパッケージ導入を取り消します。
    /// </param>
    /// <param name="cancelMaintenanceRescan">保守再検査の取消先。</param>
    /// <param name="retryLr2Sync">LR2同期の再試行先。</param>
    /// <param name="cancelPlaylistUrlDownload">URL取得の取消先。</param>
    internal MainWindowProgressStatusBarTerminals(
        Action cancelInstallPipeline,
        Action cancelMaintenanceRescan,
        Action retryLr2Sync,
        Action cancelPlaylistUrlDownload)
    {
        this.cancelPlaylistUrlDownload = cancelPlaylistUrlDownload
            ?? throw new ArgumentNullException(nameof(cancelPlaylistUrlDownload));
        this.cancelInstallPipeline = cancelInstallPipeline
            ?? throw new ArgumentNullException(nameof(cancelInstallPipeline));
        this.cancelMaintenanceRescan = cancelMaintenanceRescan
            ?? throw new ArgumentNullException(nameof(cancelMaintenanceRescan));
        this.retryLr2Sync = retryLr2Sync
            ?? throw new ArgumentNullException(nameof(retryLr2Sync));
    }

    /// <summary>
    /// 導入処理の既存管理主体へ取消を渡します。
    /// </summary>
    internal void CancelInstallPipeline() => cancelInstallPipeline();

    /// <summary>選択した行の操作を、その処理の管理主体へ一度だけ届けます。</summary>
    internal void Invoke(OperationProgressAction action)
    {
        switch (action)
        {
            case OperationProgressAction.CancelInstall: cancelInstallPipeline(); break;
            case OperationProgressAction.CancelUrlDownload: cancelPlaylistUrlDownload(); break;
            case OperationProgressAction.CancelMaintenance: cancelMaintenanceRescan(); break;
            case OperationProgressAction.RetryLr2: retryLr2Sync(); break;
        }
    }

    /// <summary>
    /// 保守再検査を既存の管理主体で取り消します。
    /// </summary>
    internal void CancelMaintenanceRescan() => cancelMaintenanceRescan();

    /// <summary>
    /// LR2同期を既存の管理主体で再試行します。
    /// </summary>
    internal void RetryLr2Sync() => retryLr2Sync();

    /// <summary>
    /// 実際の管理主体に各行の操作を接続します。
    /// </summary>
    /// <param name="viewModel">各処理の管理主体を接続しているViewModel。</param>
    /// <returns>各行を管理主体へ接続した操作先。</returns>
    internal static MainWindowProgressStatusBarTerminals Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return CreateCore(
            viewModel.PlaylistWorkspace.CancelPlaylistUrlDownload,
            viewModel.PackageInstallWorkflow.CancelAll,
            viewModel.MaintenanceRescanWorkflow.Cancel,
            viewModel.Lr2SongDbSyncWorkflow.RequestStatusBarRetry);
    }

    /// <summary>
    /// URL取得と導入をそれぞれの取消先へ接続します。
    /// </summary>
    /// <param name="cancelPlaylistUrlDownload">URL取得の取消先。</param>
    /// <param name="cancelPackageInstall">パッケージ導入の取消先。</param>
    /// <param name="cancelMaintenanceRescan">保守再検査の取消先。</param>
    /// <param name="retryLr2Sync">LR2同期の再試行先。</param>
    /// <returns>各操作を一度だけ届ける行固有の操作先。</returns>
    internal static MainWindowProgressStatusBarTerminals CreateCore(
        Action cancelPlaylistUrlDownload,
        Action cancelPackageInstall,
        Action cancelMaintenanceRescan,
        Action retryLr2Sync)
    {
        ArgumentNullException.ThrowIfNull(cancelPlaylistUrlDownload);
        ArgumentNullException.ThrowIfNull(cancelPackageInstall);
        ArgumentNullException.ThrowIfNull(cancelMaintenanceRescan);
        ArgumentNullException.ThrowIfNull(retryLr2Sync);

        return new MainWindowProgressStatusBarTerminals(
            cancelPackageInstall,
            cancelMaintenanceRescan,
            retryLr2Sync,
            cancelPlaylistUrlDownload);
    }
}
