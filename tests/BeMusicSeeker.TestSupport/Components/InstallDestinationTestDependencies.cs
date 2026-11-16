using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>機能横断のowner検証で停止要求回数を記録し、再生の実開始を伴わず停止受付を完了します。</summary>
internal sealed class NoOpChartMutationPlaybackPort : IChartMutationPlaybackPort
{
    internal int StopCount { get; private set; }
    public Task StopPlaybackForMutationAsync() { StopCount++; return Task.CompletedTask; }
}

/// <summary>表示へ到達しないownerの準備で共用し、予期しないdialog要求は元のNotSupportedExceptionで伝えます。</summary>
internal sealed class TestUiDialogService : IUiDialogService
{
    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

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
