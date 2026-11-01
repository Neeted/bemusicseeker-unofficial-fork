using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>設定とプレイリストの実媒体・workspace接続で共用する既存fixtureを保持します。</summary>
internal static class PlaylistWorkspaceFixturePorts
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


    internal static Func<DroppedInstallBatchRequest, bool> PlaylistUrlInstallSink => _ => true;

    internal static Action<Uri> PlaylistUrlBrowserOpenSink => _ => { };


    internal static Action<Exception, string> ExternalPlaylistImportWarningLog => (_, _) => { };

    internal static Action<string> ExternalPlaylistImportInfoLog => _ => { };

    internal static Action<Exception, string> BeatorajaTableUrlImportWarningLog => (_, _) => { };

    internal static Action<string> BeatorajaTableUrlImportInfoLog => _ => { };

    internal static IMainChartColumnSettingsStore PlaylistSummaryColumnSettingsStore =>
        new InMemoryMainChartColumnSettingsStore();

    internal static PlaylistSummaryBmtSortCoordinator PlaylistSummaryBmtSortCoordinator =>
        new PlaylistSummaryBmtSortCoordinator(() => null, () => []);

    internal static IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore =>
        new InMemoryKeywordSearchHistorySettingsStore();

    /// <summary>
    /// Creates an isolated Favorites settings boundary for workspace fixtures.
    /// </summary>
    internal static IKeywordSearchFavoritesSettingsStore KeywordSearchFavoritesSettingsStore =>
        new InMemoryKeywordSearchFavoritesSettingsStore();

    /// <summary>
    /// Tests which do not exercise playlist persistence intentionally keep the
    /// provider unavailable.  A test that reaches URL admission or playlist
    /// mutation must pass an owned store explicitly.
    /// </summary>
    internal static Func<BMSPlaylist> PlaylistStoreProvider => () => null!;

    internal static Func<Action, Task> PlaylistRestoreUiApplyScheduler => action =>
    {
        action();
        return Task.CompletedTask;
    };

    internal static Func<bool> PlaylistRestoreUiThreadCheck => () => true;

    internal static PlaylistPropertySaveService PlaylistPropertySaveService =>
        new PlaylistPropertySaveService(
            () => null!,
            () => null!,
            () => null!,
            () => new CustomFolderOutputSettingsSnapshot());

    internal sealed class InMemoryKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;
    }

    internal sealed class InMemoryKeywordSearchFavoritesSettingsStore : IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }

    internal sealed class PlaylistWorkspaceDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

        internal Func<UiConfirmationRequest, UiDialogResult>? ConfirmationFactory { get; set; }

        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiMessageRequest LastMessageRequest { get; private set; } = null!;

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        internal List<UiSaveFilePickerRequest> SaveFilePickerRequests { get; } = [];

        internal Queue<UiSaveFilePickerResult> SaveFilePickerResults { get; } = [];

        /// <summary>Windowを表示せず、JSON保存入口へ専用画面の確定・取消・失敗を渡します。</summary>
        internal Queue<UiWindowDialogResult<PlaylistJsonExportPaths>> JsonExportResults { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMessageRequest = request;
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastConfirmationRequest = request;
            return Task.FromResult(ConfirmationFactory?.Invoke(request) ?? ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            if (typeof(TWindow) != typeof(PlaylistJsonExportDialog) || typeof(TResult) != typeof(PlaylistJsonExportPaths))
            {
                throw new NotSupportedException();
            }
            return Task.FromResult((UiWindowDialogResult<TResult>)(object)JsonExportResults.Dequeue());
        }

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveFilePickerRequests.Add(request);
            if (SaveFilePickerResults.Count == 0)
            {
                throw new InvalidOperationException("No save file picker result was configured.");
            }
            return Task.FromResult(SaveFilePickerResults.Dequeue());
        }

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
