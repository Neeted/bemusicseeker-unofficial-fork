using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class SelectedChartResourceHealthWorkflowOwnerTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingDialogService : IUiDialogService
    {
        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal int MessageCalls { get; private set; }

        internal string LastMessage { get; private set; } = string.Empty;

        /// <summary>表示の要求を受けた時点で呼ぶ観測です。表示の時点の受付の状態を確かめるために使います。</summary>
        internal Action? OnMessage { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCalls++;
            LastMessage = request.MessageBoxText;
            OnMessage?.Invoke();
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

    /// <summary>実際の保守の呼出しが戻った時点（受付を保持している間）の状態を観測するために、本物のstoreを委譲で包む。</summary>
    internal sealed class ObservingStore(ISelectedChartResourceHealthStore inner, Action afterRescan) : ISelectedChartResourceHealthStore
    {
        public MaintenanceWorkflowResult RescanResourceHealthCharts(
            BMSLibrary library, IReadOnlyList<ChartFile> charts, LibraryFileMutationCapability capability,
            IReadOnlyList<PackageChartEntry> pendingEntries, MaintenanceReadFailureCollector readFailures)
        {
            MaintenanceWorkflowResult result = inner.RescanResourceHealthCharts(library, charts, capability, pendingEntries, readFailures);
            afterRescan();
            return result;
        }

        public void SetChartResourceWarningsIgnored(
            BMSLibrary library, IReadOnlyList<ChartFile> charts, bool unset, LibraryFileMutationCapability capability)
            => inner.SetChartResourceWarningsIgnored(library, charts, unset, capability);
    }
}
