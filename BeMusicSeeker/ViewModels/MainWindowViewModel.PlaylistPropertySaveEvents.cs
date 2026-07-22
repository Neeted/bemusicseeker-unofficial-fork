using System;
using BeMusicSeeker.Models;
using System.Windows;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    private void PlaylistWorkspacePlaylistPropertyValidationError(
        object sender,
        PlaylistPropertyValidationErrorEventArgs request)
    {
        string message = request.Error switch
        {
            PlaylistPropertyValidationError.OutputDirectoryChangedByPlaylistName => BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateChangePlaylist,
            PlaylistPropertyValidationError.InvalidOutputDirectory => BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateCheckInput,
            PlaylistPropertyValidationError.InvalidPageUri => BeMusicSeeker.Properties.Resources.Error_InvalidPageUriAbsoluteRequired,
            PlaylistPropertyValidationError.InvalidHeaderUri => BeMusicSeeker.Properties.Resources.Error_InvalidHeaderUri,
            PlaylistPropertyValidationError.InvalidDataUri => BeMusicSeeker.Properties.Resources.Error_InvalidDataUri,
            PlaylistPropertyValidationError.InvalidExternalSyncUris => BeMusicSeeker.Properties.Resources.Error_InvalidPageOrHeaderUri,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Error), request.Error, null)
        };
        ShowUiMessage(message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
    }

    private void PlaylistWorkspacePlaylistPropertyExternalSyncConfirmationRequested(
        object sender,
        PlaylistPropertyExternalSyncConfirmationRequestedEventArgs request)
    {
        request.Confirmed = ShowUiConfirmation(
            request.Enable
                ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
                : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            MessageBoxButton.OKCancel,
            request.Enable ? "Playlist sync enable confirmation" : "Playlist sync disable confirmation");
    }

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

    private void PlaylistWorkspacePlaylistFolderRemovalConfirmationRequested(
        object sender,
        PlaylistFolderRemovalConfirmationRequestedEventArgs request)
    {
        request.Confirmed = ShowUiConfirmation(
            BeMusicSeeker.Properties.Resources.Msg_remove_folder,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            "Playlist folder removal confirmation",
            MessageBoxResult.Cancel);
    }

    private void PlaylistWorkspacePlaylistTableLevelOverwriteConfirmationRequested(
        object sender,
        PlaylistTableLevelOverwriteConfirmationRequestedEventArgs request)
    {
        if (request.IsRecommendedTable)
        {
            ShowUiMessage(
                BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Hand,
                "Playlist table level overwrite recommended error notification");
            return;
        }

        request.Confirmed = ShowUiConfirmation(
            BeMusicSeeker.Properties.Resources.Msg_override_level_warning,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            "Playlist table level overwrite confirmation",
            MessageBoxResult.Cancel);
    }

    private void PlaylistWorkspacePlaylistSummaryColumnResetConfirmationRequested(
        object sender,
        PlaylistSummaryColumnResetConfirmationRequestedEventArgs request)
    {
        request.Confirmed = ShowUiConfirmation(
            BeMusicSeeker.Properties.Resources.Msg_init_column_settings,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            "Playlist summary column reset confirmation",
            MessageBoxResult.Cancel);
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

    private void PlaylistWorkspaceExternalPlaylistImportQueueSummaryReady(
        object sender,
        ExternalPlaylistImportQueueSummaryReadyEventArgs request)
    {
        ShowExternalPlaylistImportQueueSummary(request?.Summary);
    }

    private void PlaylistWorkspacePlaylistOperationNotificationPresentationRequested(
        object sender,
        PlaylistOperationNotificationPresentationRequestedEventArgs request)
    {
        PresentPlaylistOperationNotifications(request.Receipt, request.RouteName);
    }

    private void PlaylistWorkspaceExternalPlaylistImportSummaryRefreshFailed(
        object sender,
        ExternalPlaylistImportSummaryRefreshFailedEventArgs request)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + request.Exception.Message,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            "external playlist import summary refresh failure notification");
    }

    private void PlaylistWorkspaceBeatorajaTableUrlImportConfirmationRequested(
        object sender,
        BeatorajaTableUrlImportConfirmationRequestedEventArgs request)
    {
        request.Confirmed = ShowUiConfirmation(
            BeMusicSeeker.Properties.Resources.Confirm_import_beatoraja_table_urls,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            "beatoraja Table URL import confirmation");
    }

    private void PlaylistWorkspaceBeatorajaTableUrlImportNotificationRequested(
        object sender,
        BeatorajaTableUrlImportNotificationRequestedEventArgs request)
    {
        MessageBoxImage icon = request.Kind switch
        {
            BeatorajaTableUrlImportNotificationKind.Information => MessageBoxImage.Information,
            BeatorajaTableUrlImportNotificationKind.Warning => MessageBoxImage.Exclamation,
            BeatorajaTableUrlImportNotificationKind.Error => MessageBoxImage.Hand,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, null)
        };
        ShowUiMessage(request.Message, request.Caption, icon, request.RouteName);
    }

    private void PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady(
        object sender,
        BeatorajaTableUrlImportSummaryReadyEventArgs request)
    {
        ShowBeatorajaTableUrlImportSummary(request?.Summary);
    }

    private void PlaylistWorkspacePlaylistPropertyInvalidOutputDirectoryRequested(object sender, EventArgs e)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            "playlist property output directory notification");
    }

    private void PlaylistWorkspacePlaylistReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

    private void PlaylistTableRemovalWorkflowInvalidOutputDirectoryRequested(
        object sender,
        PlaylistTableRemovalInvalidOutputDirectoryEventArgs request)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            request.RouteName);
    }

    private void PlaylistWorkspacePlaylistPropertyExternalSyncFailed(
        object sender,
        PlaylistPropertyExternalSyncFailedEventArgs request)
    {
        ShowPlaylistLoadFailure(request.Exception);
    }

}
