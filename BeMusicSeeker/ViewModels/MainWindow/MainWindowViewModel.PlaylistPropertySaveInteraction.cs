using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using Ribbit.Logging;
using System.Windows;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel : IPlaylistPropertySaveInteraction
{
    void IPlaylistPropertySaveInteraction.NotifyValidationError(PlaylistPropertyValidationError error)
    {
        string message = error switch
        {
            PlaylistPropertyValidationError.OutputDirectoryChangedByPlaylistName => BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateChangePlaylist,
            PlaylistPropertyValidationError.InvalidOutputDirectory => BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateCheckInput,
            PlaylistPropertyValidationError.InvalidPageUri => BeMusicSeeker.Properties.Resources.Error_InvalidPageUriAbsoluteRequired,
            PlaylistPropertyValidationError.InvalidHeaderUri => BeMusicSeeker.Properties.Resources.Error_InvalidHeaderUri,
            PlaylistPropertyValidationError.InvalidDataUri => BeMusicSeeker.Properties.Resources.Error_InvalidDataUri,
            PlaylistPropertyValidationError.InvalidExternalSyncUris => BeMusicSeeker.Properties.Resources.Error_InvalidPageOrHeaderUri,
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };
        ShowUiMessage(message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
    }

    bool IPlaylistPropertySaveInteraction.ConfirmExternalSyncChange(bool enable)
    {
        return ShowUiConfirmation(
            enable
                ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
                : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            MessageBoxButton.OKCancel,
            enable ? "Playlist sync enable confirmation" : "Playlist sync disable confirmation");
    }

    void IPlaylistPropertySaveInteraction.BeginSyncProgress()
    {
        BeginPlaylistSyncProgressOperation();
    }

    void IPlaylistPropertySaveInteraction.ReportSyncProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        UpdatePlaylistSyncProgressStatus(snapshot);
    }

    void IPlaylistPropertySaveInteraction.EndSyncProgress()
    {
        EndPlaylistSyncProgressOperation();
    }

    void IPlaylistPropertySaveInteraction.ReplaceCurrentSelection(BMSTable oldTable, BMSTable newTable)
    {
        ReplaceCurrentPlaylistSelectionTable(oldTable, newTable);
    }

    void IPlaylistPropertySaveInteraction.RemapCurrentFolderSelection(
        BMSTable table,
        IReadOnlyDictionary<string, string> rewrittenFolders)
    {
        RemapCurrentPlaylistFolderSelection(table, rewrittenFolders);
    }

    void IPlaylistPropertySaveInteraction.InvalidateNormalReferenceSort()
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

    void IPlaylistPropertySaveInteraction.UpdateSyncStatus(PlaylistSyncAttemptResult result)
    {
        UpdatePlaylistSyncRuntimeStatus(result);
    }

    void IPlaylistPropertySaveInteraction.HandleExternalSyncFailure(BMSTable table, Uri uri, Exception exception)
    {
        NLogWrapper.FileLogger?.Warn(
            exception,
            "playlist_property_resync_failed table=" + (table?.name ?? string.Empty) + " uri=" + uri);
        UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateFailure(table, uri, exception));
        ShowPlaylistLoadFailure(exception);
    }

    void IPlaylistPropertySaveInteraction.RefreshSummary(string reason, bool invalidateTableCountCache)
    {
        RefreshPlaylistSummaryIfVisible(reason, invalidateTableCountCache);
    }

    void IPlaylistPropertySaveInteraction.ApplyEntriesChanged(BMSTable table)
    {
        ApplyPlaylistEntriesChanged(table, refreshSummaryIfVisible: true);
    }

    void IPlaylistPropertySaveInteraction.FlushNotifications(
        BMSPlaylist.OperationNotificationScope scope,
        string routeName)
    {
        FlushPlaylistOperationNotifications(scope, routeName);
    }
}
