using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// WPF の LR2 play history schema dialog を settings の中立 port に接続します。
/// </summary>
internal sealed class Lr2PlayHistorySchemaUninstallDialogPort : ILr2PlayHistorySchemaUninstallDialogPort
{
    private readonly IUiDialogService dialogs;

    internal Lr2PlayHistorySchemaUninstallDialogPort(IUiDialogService dialogs)
    {
        this.dialogs = dialogs;
    }

    public async Task<UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>> ShowAsync(
        string scoreDbPath,
        CancellationToken cancellationToken = default)
    {
        UiWindowDialogResult<Lr2PlayHistorySchemaUninstallMode> result = await dialogs.ShowWindowAsync(
            new UiWindowDialogRequest<Lr2PlayHistorySchemaUninstallDialog, Lr2PlayHistorySchemaUninstallMode>(
                () => new Lr2PlayHistorySchemaUninstallDialog(scoreDbPath),
                dialog => dialog.SelectedMode),
            cancellationToken);
        return UiDialogInteractionAdapter.FromWindowResult(result);
    }
}
