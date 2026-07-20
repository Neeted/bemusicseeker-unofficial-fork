using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceTestPorts
{
    internal static PlaylistUrlAcquisitionWorkflow CreateUrlAcquisitionWorkflow(Action<string>? log = null)
    {
        return new PlaylistUrlAcquisitionWorkflow(
            new AppPlaylistUrlDownloadGateway(),
            log ?? (_ => { }));
    }

    internal static PlaylistExternalPackageLookupService CreateExternalPackageLookupService()
    {
        return PlaylistExternalPackageLookupService.CreateDefault();
    }

    internal static Func<PlaylistUrlAcquisitionOptionsSnapshot> UrlAcquisitionOptionsProvider =>
        () => new PlaylistUrlAcquisitionOptionsSnapshot();

    internal static Func<bool> InactiveInstallQueueProvider => () => false;

    internal static Action<IReadOnlyList<string>> PlaylistUrlInstallSink => _ => { };

    internal static Action<Uri> PlaylistUrlBrowserOpenSink => _ => { };

    internal static Action<Exception, string> ExternalPlaylistImportWarningLog => (_, _) => { };

    internal static Action<string> ExternalPlaylistImportInfoLog => _ => { };

    internal static Action<Exception, string> BeatorajaTableUrlImportWarningLog => (_, _) => { };

    internal static Action<string> BeatorajaTableUrlImportInfoLog => _ => { };

    internal static IMainChartColumnSettingsStore PlaylistSummaryColumnSettingsStore =>
        new SettingsMainChartColumnSettingsStore();

    internal static PlaylistSummaryBmtSortCoordinator PlaylistSummaryBmtSortCoordinator =>
        new PlaylistSummaryBmtSortCoordinator(() => null, () => []);

    internal static IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore =>
        new InMemoryKeywordSearchHistorySettingsStore();

    internal static Func<BMSPlaylist> PlaylistStoreProvider => () => null!;

    internal static PlaylistPropertySaveService PlaylistPropertySaveService =>
        new PlaylistPropertySaveService(
            () => null!,
            () => null!,
            () => null!,
            () => new CustomFolderOutputSettingsSnapshot());

    private sealed class InMemoryKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;
    }
}
