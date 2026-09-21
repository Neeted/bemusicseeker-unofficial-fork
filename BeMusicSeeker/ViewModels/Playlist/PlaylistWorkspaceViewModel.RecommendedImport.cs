using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// Confirms and enqueues a recommended-table import.
    /// </summary>
    /// <param name="rawTag">The recommended-table URI stored in the menu item's tag.</param>
    /// <returns><see langword="true"/> when the import was enqueued.</returns>
    internal async Task<bool> EnqueueRecommendedPlaylistImportAsync(string rawTag)
    {
        Uri uri = new(rawTag);
        BMSLibrary library = getPlaylistLibrary();
        int lr2Id = library?.LR2ID ?? 0;
        if (lr2Id == 0)
        {
            await ShowPlaylistWorkspaceMessageAsync(
                UiMessageRequest.CreateError(
                    BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_error,
                    BeMusicSeeker.Properties.Resources.Error),
                "Playlist recommended table import LR2ID error notification");
            return false;
        }

        bool isUpdateMode = Regex.Match(rawTag, "mode=update").Success;
        bool confirmed = await ConfirmPlaylistWorkspaceDialogAsync(
            new UiConfirmationRequest(
                "LR2ID: " + lr2Id + (isUpdateMode
                    ? BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_update_mode
                    : BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_readonly_mode),
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.OK),
            isUpdateMode
                ? "Playlist recommended table update confirmation"
                : "Playlist recommended table read-only confirmation");
        if (!confirmed)
        {
            return false;
        }

        return EnqueueExternalPlaylistBMSTableImports([uri]);
    }

    private async Task<bool> ConfirmPlaylistWorkspaceDialogAsync(
        UiConfirmationRequest request,
        string routeName)
    {
        UiDialogResult result = await playlistWorkspaceDialogService.ConfirmAsync(request)
            .ConfigureAwait(true);
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").",
                result.Exception)
        };
    }

    private async Task ShowPlaylistWorkspaceMessageAsync(
        UiMessageRequest request,
        string routeName)
    {
        UiDialogResult result = await playlistWorkspaceDialogService.ShowMessageAsync(request)
            .ConfigureAwait(true);
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }
        if (result.Status is UiDialogStatus.Accepted
            or UiDialogStatus.Rejected
            or UiDialogStatus.CancelledByUser
            or UiDialogStatus.ClosedByUser)
        {
            return;
        }
        throw new InvalidOperationException(
            routeName + " could not be displayed (" + result.Status + ").",
            result.Exception);
    }
}
