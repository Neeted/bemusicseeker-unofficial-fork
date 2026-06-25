using System;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// window modal dialog の表示結果を表します。
/// </summary>
internal sealed class UiWindowDialogResult<TResult>
{
    internal UiWindowDialogResult(UiDialogStatus status, TResult value = default, bool? dialogResult = null, Exception error = null)
    {
        Status = status;
        Value = value;
        DialogResult = dialogResult;
        Error = error;
    }

    internal UiDialogStatus Status { get; }

    internal TResult Value { get; }

    internal bool? DialogResult { get; }

    internal Exception Error { get; }

    internal bool IsAccepted => Status == UiDialogStatus.Accepted;
}
