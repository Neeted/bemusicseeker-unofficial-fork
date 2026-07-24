using System;
using BeMusicSeeker.Models;
using System.Windows;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    private void PlaylistWorkspacePlaylistSummaryExternalSyncConfirmationRequested(
        object sender,
        PlaylistSummaryExternalSyncConfirmationRequestedEventArgs request)
    {
        request.Confirmed = ShowUiConfirmation(
            request.Enable
                ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
                : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            MessageBoxButton.OKCancel,
            request.Enable ? "Playlist summary sync enable confirmation" : "Playlist summary sync disable confirmation");
    }

    private void PlaylistWorkspacePlaylistRecommendedTableImportConfirmationRequested(
        object sender,
        PlaylistRecommendedTableImportConfirmationRequestedEventArgs request)
    {
        if (request.Lr2Id == 0)
        {
            ShowUiMessage(
                BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_error,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "Playlist recommended table import LR2ID error notification");
            return;
        }

        request.Confirmed = ShowUiConfirmation(
            "LR2ID: " + request.Lr2Id + (request.IsUpdateMode
                ? BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_update_mode
                : BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_readonly_mode),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            request.IsUpdateMode
                ? "Playlist recommended table update confirmation"
                : "Playlist recommended table read-only confirmation",
            MessageBoxResult.OK);
    }

    private void PlaylistWorkspacePlaylistReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

    private void PlaylistRemovalWorkflowInvalidOutputDirectoryRequested(
        object sender,
        PlaylistRemovalInvalidOutputDirectoryEventArgs request)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            request.RouteName);
    }

}
