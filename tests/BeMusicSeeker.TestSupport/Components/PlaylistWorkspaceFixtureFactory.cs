using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>hydrationの生産とreceipt受信を、実storeへ接続する最小workspaceを構成します。</summary>
internal static class PlaylistWorkspaceFixtureFactory
{
    /// <summary>指定したstoreとlibraryの提供元を維持し、catalog通知を即時適用するworkspaceを返します。所有者が実Task終端とDisposeを回収します。</summary>
    internal static PlaylistWorkspaceViewModel CreateBareHydrationWorkspace(
        Func<BMSPlaylist> playlistStoreProvider,
        Func<BMSLibrary> playlistLibraryProvider)
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
            playlistStoreProvider,
            PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
            playlistLibraryProvider,
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
            (_, _) => { },
            (_, _) => false,
            (_, _) => false,
            PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler,
            PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck,
            UndeclaredDialogs.Create());
        workspace.ConfigureCatalogNotificationQueue(action => action());
        return workspace;
    }

}
