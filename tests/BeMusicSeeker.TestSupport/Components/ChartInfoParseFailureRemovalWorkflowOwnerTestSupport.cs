using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class ChartInfoParseFailureRemovalWorkflowOwnerTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingStore : IChartInfoParseFailureRemovalStore
    {
        internal int CallCount { get; private set; }

        internal IReadOnlyList<string> Md5s { get; private set; } = [];

        internal Action BeforeRemove { get; set; } = null!;

        internal Action RemoveAction { get; set; } = null!;

        internal Exception Failure { get; set; } = null!;

        public void Remove(BMSLibrary library, IReadOnlyList<string> md5s, LibraryFileMutationCapability capability)
        {
            CallCount++;
            Md5s = md5s.ToArray();
            BeforeRemove?.Invoke();
            RemoveAction?.Invoke();
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = null!;

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        /// <summary>表示を要求された通知です。</summary>
        internal List<UiMessageRequest> Messages { get; } = [];

        /// <summary>表示を要求された確認の件数です。</summary>
        internal int ConfirmationCount { get; private set; }

        /// <summary>確認の表示中に実行する処理です。確認後に受付を失う競合の再現に使います。</summary>
        internal Action? OnConfirmation { get; set; }

        /// <summary>通知の表示結果です。既定はOKで閉じた結果です。×やAlt+F4で閉じた結果の再現に使います。</summary>
        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
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
