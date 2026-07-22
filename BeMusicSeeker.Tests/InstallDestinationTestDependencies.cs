using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

internal sealed class NoOpPendingPackageMutationPlaybackPort : IPendingPackageMutationPlaybackPort
{
    public void StopIfPlayingCharts(IReadOnlyList<ChartFile> charts)
    {
    }
}

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

internal sealed class NoOpSelectedChartMutationActivityPort : ISelectedChartMutationActivityPort
{
    public void BeginActivity()
    {
    }

    public void EndActivity()
    {
    }
}

internal sealed class NoOpSelectedChartMutationRefreshPort : ISelectedChartMutationRefreshPort
{
    public void BeginRefreshSuppression(SelectedChartMutationRefreshScope scope)
    {
    }

    public void EndRefreshSuppression()
    {
    }

    public void ApplyLibraryPathMutationRefresh()
    {
    }

    public void ApplyEncodingRefresh()
    {
    }
}
