using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Views.Dialogs;

internal sealed class PendingDeleteConfirmationDialogPort : IPendingDeleteConfirmationDialogPort
{
    private readonly IUiDialogService dialogs;

    internal PendingDeleteConfirmationDialogPort(IUiDialogService dialogs)
    {
        this.dialogs = dialogs;
    }

    public async Task<UiInteractionResult<bool>> ShowAsync()
    {
        UiWindowDialogResult<bool> result = await dialogs.ShowWindowAsync(
            new UiWindowDialogRequest<PendingDeleteConfirmDialog, bool>(
                () => new PendingDeleteConfirmDialog(),
                dialog => dialog.DeleteFolderWhenNoBmsChecked));
        return UiDialogInteractionAdapter.FromWindowResult(result);
    }
}
