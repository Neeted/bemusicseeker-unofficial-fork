using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

internal sealed class TestInstallDestinationMutationPresentation : IInstallDestinationMutationPresentation
{
    public void BeginActivity()
    {
    }

    public void BeginRefreshSuppression(InstallDestinationRefreshScope scope)
    {
    }

    public void EndRefreshSuppression()
    {
    }

    public void EndActivity()
    {
    }

    public void UpdateTransientStates(IEnumerable<ChartFile> charts)
    {
    }

    public void InvalidateInstallDestinationSort()
    {
    }

    public void RefreshIdentitySortKey()
    {
    }

    public void RequestDisplayRefresh()
    {
    }

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
