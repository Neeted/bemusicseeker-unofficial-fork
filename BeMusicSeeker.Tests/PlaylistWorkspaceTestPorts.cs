using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

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

    internal static Action PlaylistUrlInstallTreeExpansionSink => () => { };

    internal static Action<PlaylistSummarySelectionRestoreRequest> PlaylistSummarySelectionRestoreSink => _ => { };

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
            where TWindow : Window => throw new NotSupportedException();

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

    private sealed class InMemoryKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;
    }
}
