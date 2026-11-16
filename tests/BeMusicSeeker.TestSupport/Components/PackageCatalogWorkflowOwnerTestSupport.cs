using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class PackageCatalogWorkflowOwnerTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class ControlledPlayback(Func<Task> stop) : IChartMutationPlaybackPort
    {
        internal Func<Task> Stop { get; set; } = stop;
        public Task StopPlaybackForMutationAsync() => Stop();
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        /// <summary>表示を要求された通知です。受付使用中の警告以外の通知を期待しない検査は、件数で余分な通知を検出します。</summary>
        internal List<UiMessageRequest> Messages { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        /// <summary>設定すると確認をこの完了まで未完了のまま返します。未設定なら <see cref="ConfirmationResult"/> で同期的に完了します。</summary>
        internal TaskCompletionSource<UiDialogResult>? PendingConfirmation { get; set; }

        /// <summary>設定すると確認の要求を受けた時点で完了し、確認への到達を生成元の通知で待てます。</summary>
        internal TaskCompletionSource? ConfirmationReached { get; set; }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            ConfirmationReached?.TrySetResult();
            if (PendingConfirmation != null)
            {
                return PendingConfirmation.Task;
            }
            return Task.FromResult(ConfirmationResult
                ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
