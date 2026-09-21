using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

internal sealed class BlockingConfirmationDialogService : IUiDialogService
{
    internal TaskCompletionSource<UiConfirmationRequest> ConfirmationShown { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<bool> ConfirmationReleased { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void ReleaseConfirmation()
    {
        ConfirmationReleased.TrySetResult(true);
    }

    public Task<UiDialogResult> ShowMessageAsync(
        UiMessageRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<UiDialogResult> ConfirmAsync(
        UiConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ConfirmationShown.TrySetResult(request);
        return WaitForConfirmationAsync(cancellationToken);
    }

    private async Task<UiDialogResult> WaitForConfirmationAsync(CancellationToken cancellationToken)
    {
        await ConfirmationReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
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
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<UiProgressResult> RunWithProgressAsync(
        UiProgressRequest request,
        Func<UiProgressContext, Task> operation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
