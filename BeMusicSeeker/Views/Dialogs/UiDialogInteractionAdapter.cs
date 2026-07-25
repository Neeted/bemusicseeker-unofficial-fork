using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views.Dialogs;

internal static class UiDialogInteractionAdapter
{
    internal static UiInteractionResult<TResult> FromWindowResult<TResult>(UiWindowDialogResult<TResult> result)
    {
        if (result == null)
        {
            return new UiInteractionResult<TResult>(UiInteractionStatus.NotShown);
        }

        return new UiInteractionResult<TResult>(
            result.Status switch
            {
                UiDialogStatus.Accepted => UiInteractionStatus.Accepted,
                UiDialogStatus.Rejected => UiInteractionStatus.Rejected,
                UiDialogStatus.CancelledByUser => UiInteractionStatus.CancelledByUser,
                UiDialogStatus.ClosedByUser => UiInteractionStatus.ClosedByUser,
                UiDialogStatus.NotShown => UiInteractionStatus.NotShown,
                UiDialogStatus.AppClosing => UiInteractionStatus.AppClosing,
                UiDialogStatus.OwnerUnavailable => UiInteractionStatus.OwnerUnavailable,
                UiDialogStatus.DispatcherUnavailable => UiInteractionStatus.DispatcherUnavailable,
                UiDialogStatus.Failed => UiInteractionStatus.Failed,
                _ => UiInteractionStatus.Failed,
            },
            result.Value,
            result.Error);
    }
}
