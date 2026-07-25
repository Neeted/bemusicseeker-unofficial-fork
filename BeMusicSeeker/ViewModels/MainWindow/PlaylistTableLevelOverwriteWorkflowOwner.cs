using System;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns playlist-table level overwrite confirmation, mutation, and blocked-state presentation.
/// </summary>
internal sealed class PlaylistTableLevelOverwriteWorkflowOwner
{
    private readonly IUiDialogService dialogs;

    private readonly Func<BMSLibrary> playlistLibraryProvider;

    internal PlaylistTableLevelOverwriteWorkflowOwner(
        IUiDialogService dialogs,
        Func<BMSLibrary> playlistLibraryProvider)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.playlistLibraryProvider = playlistLibraryProvider
            ?? throw new ArgumentNullException(nameof(playlistLibraryProvider));
    }

    internal async Task OverwriteAsync(BMSTable table)
    {
        if (table == null)
        {
            return;
        }

        if (IsRecommendedTable(table))
        {
            await ShowMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended,
                    BeMusicSeeker.Properties.Resources.Confirm,
                    MessageBoxImage.Hand,
                    "Playlist table level overwrite recommended error notification")
                .ConfigureAwait(true);
            return;
        }

        if (!await ConfirmAsync(
                BeMusicSeeker.Properties.Resources.Msg_override_level_warning,
                "Playlist table level overwrite confirmation")
            .ConfigureAwait(true))
        {
            return;
        }

        BmsFileLevelOverwriteOutcome outcome = await Task.Run(() =>
        {
            BMSLibrary library = playlistLibraryProvider()
                ?? throw new InvalidOperationException("Playlist library is not available.");
            return library.ReplaceBmsFileLevelByTableEntryLevel(table);
        }).ConfigureAwait(false);
        if (outcome == BmsFileLevelOverwriteOutcome.BlockedByLr2Synchronization)
        {
            await ShowMessageAsync(
                    BeMusicSeeker.Properties.Resources.Warn_Lr2SongDbSyncRunning,
                    BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
                    MessageBoxImage.Exclamation,
                    "Playlist table level overwrite LR2 synchronization warning")
                .ConfigureAwait(true);
        }
    }

    private async Task<bool> ConfirmAsync(string message, string routeName)
    {
        UiDialogResult result = await dialogs.ConfirmAsync(
                new UiConfirmationRequest(
                    message,
                    BeMusicSeeker.Properties.Resources.Confirm,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question,
                    MessageBoxResult.Cancel))
            .ConfigureAwait(true);
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }
        if (result.IsAccepted)
        {
            return true;
        }
        if (result.Status is UiDialogStatus.Rejected
            or UiDialogStatus.CancelledByUser
            or UiDialogStatus.ClosedByUser)
        {
            return false;
        }
        throw result.Exception
            ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").");
    }

    private async Task ShowMessageAsync(
        string message,
        string caption,
        MessageBoxImage icon,
        string routeName)
    {
        UiDialogResult result = await dialogs.ShowMessageAsync(
                new UiMessageRequest(
                    message,
                    caption,
                    MessageBoxButton.OK,
                    icon,
                    MessageBoxResult.OK))
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
        throw result.Exception
            ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").");
    }

    private static bool IsRecommendedTable(BMSTable table)
    {
        return !string.IsNullOrWhiteSpace(table.page_url)
            && table.page_url.StartsWith("bmseeker:table.recommended");
    }
}
