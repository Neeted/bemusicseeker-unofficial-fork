using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 表とフォルダーの削除確認を行い、Pを保存・必須出力・通知の実終端まで保持します。
/// </summary>
internal sealed class PlaylistRemovalWorkflowOwner
{
    private readonly IUiDialogService dialogs;

    private readonly Func<BMSPlaylist> playlistStoreProvider;

    private readonly Func<BMSLibrary> playlistLibraryProvider;

    private readonly Func<LR2Config> lr2ConfigProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<BMSPlaylist, PlaylistWorkspaceMutationKind, IDisposable> tryBeginPlaylistMutation;

    internal event EventHandler ReferenceSortInvalidationRequested;

    internal event EventHandler SummaryRefreshRequested;

    internal event EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> OperationNotificationPresentationRequested;

    internal event EventHandler<PlaylistRemovalInvalidOutputDirectoryEventArgs> InvalidOutputDirectoryRequested;

    internal event EventHandler<PlaylistWorkspaceMutationRejectedEventArgs> MutationRejected;

    internal event EventHandler<PlaylistFolderRemovalAppliedEventArgs> FolderRemovalApplied;

    /// <summary>
    /// 削除の確認・保存・通知を接続します。フォルダー削除は取得した論理受付を通知終端まで保持します。
    /// </summary>
    internal PlaylistRemovalWorkflowOwner(
        IUiDialogService dialogs,
        Func<BMSPlaylist> playlistStoreProvider,
        Func<BMSLibrary> playlistLibraryProvider,
        Func<LR2Config> lr2ConfigProvider,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        Func<BMSPlaylist, PlaylistWorkspaceMutationKind, IDisposable> tryBeginPlaylistMutation = null)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.playlistStoreProvider = playlistStoreProvider ?? throw new ArgumentNullException(nameof(playlistStoreProvider));
        this.playlistLibraryProvider = playlistLibraryProvider ?? throw new ArgumentNullException(nameof(playlistLibraryProvider));
        this.lr2ConfigProvider = lr2ConfigProvider ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? throw new ArgumentNullException(nameof(customFolderOutputSettingsProvider));
        this.tryBeginPlaylistMutation = tryBeginPlaylistMutation;
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

        await RemoveTablesAsync([table], applySelectionBeforeMutation).ConfigureAwait(false);
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

        if (table.is_external_sync)
        {
            MutationRejected?.Invoke(this, new PlaylistWorkspaceMutationRejectedEventArgs(
                PlaylistWorkspaceMutationKind.RemoveFolder, isBusy: false, isStale: false));
            return;
        }

        BMSPlaylist store = GetPlaylistStore();
        using IDisposable admission = TryAccept(store, PlaylistWorkspaceMutationKind.RemoveFolder);
        if (admission == null) { return; }
        using LibraryFileMutationCapability capability = store.CreatePlaylistMutationCapability(admission);
        await RemoveFolderAsync(store, table, folderName, capability).ConfigureAwait(false);
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

    private IDisposable TryAccept(BMSPlaylist store, PlaylistWorkspaceMutationKind kind)
    {
        if (tryBeginPlaylistMutation != null) { return tryBeginPlaylistMutation(store, kind); }
        if (store.TryEnterPlaylistMutation(out IDisposable admission)) { return admission; }
        MutationRejected?.Invoke(this, new PlaylistWorkspaceMutationRejectedEventArgs(kind, isBusy: true, isStale: false));
        return null;
    }

    private async Task RemoveTablesAsync(IEnumerable<BMSTable> tables, Action applySelectionBeforeMutation = null)
    {
        BMSPlaylist store = GetPlaylistStore();
        using IDisposable admission = TryAccept(store, PlaylistWorkspaceMutationKind.RemoveTable);
        if (admission == null) { return; }
        using LibraryFileMutationCapability authority = store.CreatePlaylistMutationCapability(admission);
        List<BMSTable> requestedTables = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (requestedTables.Count == 0) { return; }
        applySelectionBeforeMutation?.Invoke();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = store.OperationNotificationOwner.BeginSession();
        List<BMSTable> removed = [];
        List<Exception> failures = [];
        try
        {
            CustomFolderOutputSettingsSnapshot settings = customFolderOutputSettingsProvider()
                ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
            List<BMSTable> prepared = [];
            foreach (BMSTable requested in requestedTables)
            {
                BMSTable table = store.ResolveActivePlaylistTableForMutation(requested);
                if (table == null)
                {
                    MutationRejected?.Invoke(this, new PlaylistWorkspaceMutationRejectedEventArgs(
                        PlaylistWorkspaceMutationKind.RemoveTable, isBusy: false, isStale: true));
                    continue;
                }
                try
                {
                    await Task.Run(() => PrepareTableRemoval(store, table, settings, authority)).ConfigureAwait(false);
                    prepared.Add(table);
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            try
            {
                (IReadOnlyList<BMSTable> Removed, Exception PublicationFailure) result = await Task.Run(() => store.RemoveBMSTables(prepared, authority)).ConfigureAwait(false);
                removed.AddRange(result.Removed);
                if (result.PublicationFailure != null) { failures.Add(result.PublicationFailure); }
            }
            catch (Exception failure) { failures.Add(failure); }
            foreach (BMSTable table in removed)
            {
                try { PublishRemovedTable(table, settings); }
                catch (Exception failure) { failures.Add(failure); }
            }
            try { await store.BmtOutput.RemoveTablesAsync(removed, "playlist_remove_tables", authority).ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
            try
            {
                ReferenceSortInvalidationRequested?.Invoke(this, EventArgs.Empty);
                SummaryRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        finally
        {
            try
            {
                OperationNotificationPresentationRequested?.Invoke(this,
                    new PlaylistOperationNotificationPresentationRequestedEventArgs(notificationSession.TakeReceipt(),
                        "playlist remove custom folder notification"));
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures).Flatten(); }
    }

    private static void PrepareTableRemoval(BMSPlaylist playlistStore, BMSTable table,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability)
    {
        if (settings.OperationModeLR2DB && !string.IsNullOrWhiteSpace(table.Output_dir))
        {
            playlistStore.RemoveCustomFolder(table, settings, capability);
        }
    }

    private void PublishRemovedTable(BMSTable table, CustomFolderOutputSettingsSnapshot settings)
    {
        GetPlaylistLibrary().RemoveReferenceBMSTables(table);
        if (settings.OperationModeLR2DB && table.is_root_folder && !string.IsNullOrWhiteSpace(table.Output_dir))
        {
            LR2Config lr2config = lr2ConfigProvider() ?? throw new InvalidOperationException("LR2 config provider is not configured.");
            lr2config.RemoveBMSSearchDirectories([ResolveCustomFolderOutputDirectory(table,
                "playlist remove custom folder output directory notification", settings)]);
            lr2config.Save();
        }
    }

    private async Task RemoveFolderAsync(BMSPlaylist playlistStore, BMSTable table, string folderName,
        LibraryFileMutationCapability capability)
    {
        if (!CanRemoveFolder(table)) { return; }
        BMSTable activeTable = playlistStore.ResolveActivePlaylistTableForMutation(table);
        if (activeTable == null)
        {
            MutationRejected?.Invoke(
                this,
                new PlaylistWorkspaceMutationRejectedEventArgs(
                    PlaylistWorkspaceMutationKind.RemoveFolder,
                    isBusy: false,
                    isStale: true));
            return;
        }
        if (!playlistStore.ContainsPlaylistFolderForMutation(activeTable, folderName))
        {
            MutationRejected?.Invoke(
                this,
                new PlaylistWorkspaceMutationRejectedEventArgs(
                    PlaylistWorkspaceMutationKind.RemoveFolder,
                    isBusy: false,
                    isStale: true));
            return;
        }
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession =
            playlistStore.OperationNotificationOwner.BeginSession();
        try
        {
            if (!CanRemoveFolder(activeTable)
                || !playlistStore.ContainsBMSTable(activeTable)
                || !playlistStore.ContainsPlaylistFolderForMutation(activeTable, folderName)
                || !await Task.Run(() => playlistStore.RemoveFolderBMSTable(activeTable, folderName, capability: capability)).ConfigureAwait(false))
            {
                return;
            }

            await playlistStore.BmtOutput.ExportTablesAsync([activeTable], "playlist_remove_folder", capability).ConfigureAwait(false);
            FolderRemovalApplied?.Invoke(
                this,
                new PlaylistFolderRemovalAppliedEventArgs(activeTable, folderName));
        }
        catch (PlaylistMutationPostCommitException)
        {
            if (playlistStore.ContainsBMSTable(activeTable))
            {
                try
                {
                    FolderRemovalApplied?.Invoke(
                        this,
                        new PlaylistFolderRemovalAppliedEventArgs(activeTable, folderName));
                }
                catch
                {
                    // UI/reference publication is best effort after a durable mutation.
                }
            }
            throw;
        }
        finally
        {
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
