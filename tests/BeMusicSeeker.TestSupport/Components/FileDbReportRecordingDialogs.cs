using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>終端通知と実folder変更経路が渡した要求を記録し、表示失敗を局所注入します。</summary>
internal sealed class FileDbReportRecordingDialogs : IUiDialogService, IBmsLibraryDialogService
{
    internal List<UiMessageRequest> Messages { get; } = [];
    /// <summary>モデルが同期の表示で要求した件数と、待たない投函で届いた通知の件数の合計です。モデルの窓口へ何も届かないことの表明が、新しい経路の到達を見逃さないようにします。</summary>
    internal int ModelMessages => modelShowCount + PostedMessages.Count;

    private int modelShowCount;
    internal Action? OnMessage { get; set; }
    internal Exception? MessageFailure { get; set; }
    internal Func<UiMessageRequest, Task<UiDialogResult>>? MessageHandler { get; set; }

    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        Messages.Add(request);
        OnMessage?.Invoke();
        if (MessageFailure != null)
        {
            throw MessageFailure;
        }

        if (MessageHandler != null)
        {
            return MessageHandler(request);
        }

        return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    }

    public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
    {
        modelShowCount++;
        return UiDialogDefaultResult.OK;
    }

    /// <summary>待たない投函で届いた通知を記録します。件数は <see cref="ModelMessages"/> にも含みます。</summary>
    internal List<BMSLibrary.OperationDialogMessage> PostedMessages { get; } = [];

    public void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress)
    {
        PostedMessages.AddRange(messages);
    }

    public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken = default) where TWindow : Window => throw new NotSupportedException();
    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
