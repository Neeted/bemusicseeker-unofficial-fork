using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class MaintenanceRescanWorkflowOwnerTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class AcceptedDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal int ConfirmationCalls { get; private set; }

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        /// <summary>表示を要求された通知です。</summary>
        internal List<UiMessageRequest> Messages { get; } = [];

        /// <summary>確認の表示中に実行する処理です。確認後に受付を失う競合の再現に使います。</summary>
        internal Action? OnConfirmation { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCalls++;
            LastConfirmationRequest = request;
            OnConfirmation?.Invoke();
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
