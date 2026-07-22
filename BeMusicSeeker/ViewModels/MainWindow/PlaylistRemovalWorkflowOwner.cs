using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns playlist table and folder removal confirmation, mutation, and terminal presentation facts.
/// </summary>
internal sealed class PlaylistRemovalWorkflowOwner
{
    private readonly IUiDialogService dialogs;

    private readonly Func<BMSPlaylist> playlistStoreProvider;

    private readonly Func<BMSLibrary> playlistLibraryProvider;

    private readonly Func<LR2Config> lr2ConfigProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    internal event EventHandler ReferenceSortInvalidationRequested;

    internal event EventHandler SummaryRefreshRequested;

    internal event EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> OperationNotificationPresentationRequested;

    internal event EventHandler<PlaylistRemovalInvalidOutputDirectoryEventArgs> InvalidOutputDirectoryRequested;

    internal event EventHandler<PlaylistWorkspaceMutationRejectedEventArgs> MutationRejected;

    internal event EventHandler<PlaylistFolderRemovalAppliedEventArgs> FolderRemovalApplied;

    internal PlaylistRemovalWorkflowOwner(
        IUiDialogService dialogs,
        Func<BMSPlaylist> playlistStoreProvider,
        Func<BMSLibrary> playlistLibraryProvider,
        Func<LR2Config> lr2ConfigProvider,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.playlistStoreProvider = playlistStoreProvider ?? throw new ArgumentNullException(nameof(playlistStoreProvider));
        this.playlistLibraryProvider = playlistLibraryProvider ?? throw new ArgumentNullException(nameof(playlistLibraryProvider));
        this.lr2ConfigProvider = lr2ConfigProvider ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? throw new ArgumentNullException(nameof(customFolderOutputSettingsProvider));
    }

    internal async Task RemoveTreeTableAsync(BMSTable table, Action applySelectionBeforeMutation)
    {
        if (table == null)
        {
            return;
        }
        if (applySelectionBeforeMutation == null)
        {
            throw new ArgumentNullException(nameof(applySelectionBeforeMutation));
        }
        if (!await ConfirmAsync(
                BeMusicSeeker.Properties.Resources.Msg_remove_playlist,
                "Playlist table removal confirmation")
            .ConfigureAwait(true))
        {
            return;
        }

        applySelectionBeforeMutation();
        await RemoveTablesAsync([table]).ConfigureAwait(false);
    }

    internal async Task RemoveSummaryRowsAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> selectedRows = [.. (rows ?? []).Where(row => row != null)];
        if (selectedRows.Count == 0)
        {
            return;
        }
        if (!await ConfirmAsync(
                BeMusicSeeker.Properties.Resources.Msg_remove_playlist,
                "Playlist summary removal confirmation")
            .ConfigureAwait(true))
        {
            return;
        }

        await RemoveTablesAsync(selectedRows.Select(row => row.TableRef)).ConfigureAwait(false);
    }

    internal async Task RemoveFolderAsync(BMSTable table, PlaylistFolderNode folder)
    {
        if (table == null || table.is_external_sync || folder?.IsEditable != true)
        {
            return;
        }

        string folderName = folder.FolderName;
        if (!await ConfirmAsync(
                BeMusicSeeker.Properties.Resources.Msg_remove_folder,
                "Playlist folder removal confirmation")
            .ConfigureAwait(true))
        {
            return;
        }

        await Task.Run(() => RemoveFolder(table, folderName)).ConfigureAwait(false);
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

    private async Task RemoveTablesAsync(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> requestedTables = [.. (tables ?? []).Where(table => table != null)];
        if (requestedTables.Count == 0)
        {
            return;
        }
        await Task.Run(() => RemoveTables(requestedTables)).ConfigureAwait(false);
    }

    private void RemoveTables(IReadOnlyList<BMSTable> tables)
    {
        CustomFolderOutputSettingsSnapshot settings = null;
        bool settingsLoaded = false;
        CustomFolderOutputSettingsSnapshot GetSettingsSnapshot()
        {
            if (!settingsLoaded)
            {
                settings = customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
                settingsLoaded = true;
            }
            return settings;
        }

        foreach (BMSTable table in tables)
        {
            RemoveTableCore(table, GetSettingsSnapshot);
        }
        SummaryRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveTableCore(
        BMSTable table,
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        BMSPlaylist playlistStore = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession =
            playlistStore.OperationNotificationOwner.BeginSession();
        try
        {
            CustomFolderOutputSettingsSnapshot settings = settingsProvider();
            BMSLibrary library = GetPlaylistLibrary();
            if (settings.OperationModeLR2DB && !string.IsNullOrWhiteSpace(table.Output_dir))
            {
                playlistStore.RemoveCustomFolder(table, settings);
            }

            BMSTable removedTable = playlistStore.RemoveBMSTable(table);
            if (settings.OperationModeLR2DB
                && table.is_root_folder
                && !string.IsNullOrWhiteSpace(table.Output_dir))
            {
                LR2Config lr2config = lr2ConfigProvider()
                    ?? throw new InvalidOperationException("LR2 config provider is not configured.");
                string customFolderOutputDirectory = ResolveCustomFolderOutputDirectory(
                    table,
                    "playlist remove custom folder output directory notification",
                    settings);
                lr2config.RemoveBMSSearchDirectories([customFolderOutputDirectory]);
                lr2config.Save();
            }

            if (removedTable != null)
            {
                library.RemoveReferenceBMSTables(removedTable);
            }

            ReferenceSortInvalidationRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            OperationNotificationPresentationRequested?.Invoke(
                this,
                new PlaylistOperationNotificationPresentationRequestedEventArgs(
                    notificationSession.TakeReceipt(),
                    "playlist remove custom folder notification"));
        }
    }

    private void RemoveFolder(BMSTable table, string folderName)
    {
        if (!CanRemoveFolder(table))
        {
            return;
        }

        BMSPlaylist playlistStore = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession =
            playlistStore.OperationNotificationOwner.BeginSession();
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            if (!CanRemoveFolder(table)
                || !playlistStore.ContainsBMSTable(table)
                || !playlistStore.RemoveFolderBMSTable(table, folderName))
            {
                return;
            }

            FolderRemovalApplied?.Invoke(
                this,
                new PlaylistFolderRemovalAppliedEventArgs(table, folderName));
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
            OperationNotificationPresentationRequested?.Invoke(
                this,
                new PlaylistOperationNotificationPresentationRequestedEventArgs(
                    notificationSession.TakeReceipt(),
                    "playlist remove folder notification"));
        }
    }

    private bool CanRemoveFolder(BMSTable table)
    {
        if (!table.is_external_sync)
        {
            return true;
        }

        MutationRejected?.Invoke(
            this,
            new PlaylistWorkspaceMutationRejectedEventArgs(PlaylistWorkspaceMutationKind.RemoveFolder));
        return false;
    }

    private string ResolveCustomFolderOutputDirectory(
        BMSTable table,
        string routeName,
        CustomFolderOutputSettingsSnapshot settings)
    {
        try
        {
            return BMSPlaylist.GetCustomFolderOutputDirectory(
                table,
                settings.LR2CustomFolderOutputBaseDir,
                settings.LR2CustomFolderOutputBaseDirRootType,
                settings.LR2CustomFolderAdditionalOutputBaseDirs);
        }
        catch (ArgumentNullException)
        {
            InvalidOutputDirectoryRequested?.Invoke(
                this,
                new PlaylistRemovalInvalidOutputDirectoryEventArgs(routeName));
            throw;
        }
    }

    private BMSPlaylist GetPlaylistStore()
    {
        return playlistStoreProvider()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
    }

    private BMSLibrary GetPlaylistLibrary()
    {
        return playlistLibraryProvider()
            ?? throw new InvalidOperationException("Playlist library is not available.");
    }
}

internal sealed class PlaylistRemovalInvalidOutputDirectoryEventArgs : EventArgs
{
    internal PlaylistRemovalInvalidOutputDirectoryEventArgs(string routeName)
    {
        RouteName = routeName ?? string.Empty;
    }

    internal string RouteName { get; }
}

internal sealed class PlaylistFolderRemovalAppliedEventArgs : EventArgs
{
    internal PlaylistFolderRemovalAppliedEventArgs(BMSTable table, string folderName)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        FolderName = folderName ?? string.Empty;
    }

    internal BMSTable Table { get; }

    internal string FolderName { get; }
}
