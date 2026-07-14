using System;
using BeMusicSeeker.Models;
using Ribbit.Logging;
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

    private void PlaylistWorkspacePlaylistPropertyInvalidOutputDirectoryRequested(object sender, EventArgs e)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            "playlist property output directory notification");
    }

    private void PlaylistWorkspacePlaylistPropertySyncStarted(object sender, EventArgs e)
    {
        BeginPlaylistSyncProgressOperation();
    }

    private void PlaylistWorkspacePlaylistPropertySyncFinished(object sender, EventArgs e)
    {
        EndPlaylistSyncProgressOperation();
    }

    private void PlaylistWorkspacePlaylistPropertyReferenceTableReplaced(
        object sender,
        PlaylistReferenceTableReplacedEventArgs request)
    {
        InvokeMainChartListPresentationAction(() =>
            PlaylistWorkspace.ReplaceCurrentPlaylistDetailSelectionTable(request.OldTable, request.NewTable));
    }

    private void PlaylistWorkspacePlaylistPropertyFolderSelectionRemapped(
        object sender,
        PlaylistPropertyFolderSelectionRemappedEventArgs request)
    {
        PlaylistWorkspace.RemapCurrentPlaylistDetailFolderSelection(request.Table, request.RewrittenFolders);
    }

    private void PlaylistWorkspacePlaylistPropertyReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

    private void PlaylistWorkspacePlaylistReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

    private void PlaylistWorkspacePlaylistTableRemovalInvalidOutputDirectoryRequested(
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
        NLogWrapper.FileLogger?.Warn(
            request.Exception,
            "playlist_property_resync_failed table="
                + (request.Table?.name ?? string.Empty)
                + " uri="
                + (request.Uri?.ToString() ?? string.Empty));
        ShowPlaylistLoadFailure(request.Exception);
    }

    private void PlaylistWorkspacePlaylistPropertyNotificationsFlushRequested(
        object sender,
        PlaylistPropertyNotificationsFlushRequestedEventArgs request)
    {
        FlushPlaylistOperationNotifications(request.Scope, request.RouteName);
    }
}
