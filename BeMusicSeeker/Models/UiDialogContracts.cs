using System;

namespace BeMusicSeeker.Models;

internal enum UiDialogButton
{
    OK,
    OKCancel,
    YesNo,
    YesNoCancel,
}

internal enum UiDialogIcon
{
    None,
    Hand,
    Question,
    Exclamation,
    Asterisk,
    Information,
    Warning,
}

internal enum UiDialogDefaultResult
{
    None,
    OK,
    Cancel,
    Yes,
    No,
}

internal enum UiInteractionStatus
{
    Accepted,
    Rejected,
    CancelledByUser,
    ClosedByUser,
    NotShown,
    AppClosing,
    OwnerUnavailable,
    DispatcherUnavailable,
    Failed,
}

internal sealed class UiInteractionResult<TResult>
{
    internal UiInteractionResult(
        UiInteractionStatus status,
        TResult value = default,
        Exception error = null)
    {
        Status = status;
        Value = value;
        Error = error;
    }

    internal UiInteractionStatus Status { get; }

    internal TResult Value { get; }

    internal Exception Error { get; }

    internal bool IsAccepted => Status == UiInteractionStatus.Accepted;
}
