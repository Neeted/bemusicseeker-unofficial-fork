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
/// 表とフォルダーの削除確認を行い、Pを保存・必須出力の実終端まで保持します。
/// 通知は操作の収集先へ集め、Pを解放した後に、見出し・重要度ごとにまとめて表示を依頼します。表示の完了は待ちません。
/// </summary>
internal sealed class PlaylistRemovalWorkflowOwner
{
    private readonly IUiDialogService dialogs;

    private readonly Func<BMSPlaylist> playlistStoreProvider;

    private readonly Func<BMSLibrary> playlistLibraryProvider;

    private readonly Func<Lr2ConfigOwner> lr2ConfigProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<BMSPlaylist, PlaylistWorkspaceMutationKind, IDisposable> tryBeginPlaylistMutation;

    private readonly PlaylistOperationNoticePresenter noticePresenter;

    internal event EventHandler SummaryRefreshRequested;

    internal event EventHandler<PlaylistFolderRemovalAppliedEventArgs> FolderRemovalApplied;

    /// <summary>
    /// 削除の確認・保存・通知を接続します。削除は取得した論理受付を保存・必須出力の終端まで保持し、通知は受付の解放後に依頼します。
    /// </summary>
    /// <param name="noticePresenter">通知の表示役。省略時は、終了の要求を持たない表示役を窓口から作ります。</param>
    internal PlaylistRemovalWorkflowOwner(
        IUiDialogService dialogs,
        Func<BMSPlaylist> playlistStoreProvider,
        Func<BMSLibrary> playlistLibraryProvider,
        Func<Lr2ConfigOwner> lr2ConfigProvider,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        Func<BMSPlaylist, PlaylistWorkspaceMutationKind, IDisposable> tryBeginPlaylistMutation = null,
        PlaylistOperationNoticePresenter noticePresenter = null)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.playlistStoreProvider = playlistStoreProvider ?? throw new ArgumentNullException(nameof(playlistStoreProvider));
        this.playlistLibraryProvider = playlistLibraryProvider ?? throw new ArgumentNullException(nameof(playlistLibraryProvider));
        this.lr2ConfigProvider = lr2ConfigProvider ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? throw new ArgumentNullException(nameof(customFolderOutputSettingsProvider));
        this.tryBeginPlaylistMutation = tryBeginPlaylistMutation;
        this.noticePresenter = noticePresenter ?? new PlaylistOperationNoticePresenter(this.dialogs, () => false);
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
        if (await RejectIfBusyBeforeConfirmationAsync("playlist remove table busy notification").ConfigureAwait(true)) { return; }
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
        if (await RejectIfBusyBeforeConfirmationAsync("playlist remove summary rows busy notification").ConfigureAwait(true)) { return; }
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
        if (await RejectIfBusyBeforeConfirmationAsync("playlist remove folder busy notification").ConfigureAwait(true)) { return; }
        if (!await ConfirmAsync(
                BeMusicSeeker.Properties.Resources.Msg_remove_folder,
                "Playlist folder removal confirmation")
            .ConfigureAwait(true))
        {
            return;
        }

        if (table.is_external_sync)
        {
            await noticePresenter.BeginRequest(
                PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create(
                    [PlaylistOperationNotices.Rejection(PlaylistWorkspaceMutationKind.RemoveFolder)]),
                "playlist remove folder rejection notification").ConfigureAwait(false);
            return;
        }

        BMSPlaylist store = GetPlaylistStore();
        // 通知の収集先はPより外側に置き、Pを解放した後に表示を依頼します。表示の失敗は削除の結果へ加えません。
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = store.OperationNotificationOwner.BeginSession();
        try
        {
            using IDisposable admission = TryAccept(store, PlaylistWorkspaceMutationKind.RemoveFolder, notificationSession);
            if (admission == null) { return; }
            using LibraryFileMutationCapability capability = store.CreatePlaylistMutationCapability(admission);
            await RemoveFolderAsync(store, table, folderName, capability, notificationSession).ConfigureAwait(false);
        }
        finally
        {
            await noticePresenter.BeginRequest(notificationSession.TakeReceipt(), "playlist remove folder notification").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 確認の前に、受付（P）の可否を取得・予約せずに読み取ります。競合する見込みなら副作用なしで案内し、trueを返します（確認は出しません）。
    /// 確認の間も他の操作が受付を取得できるため、確認後の実際の取得で競合した場合は取得側が一回案内します。
    /// </summary>
    private async Task<bool> RejectIfBusyBeforeConfirmationAsync(string routeName)
    {
        // 未接続の保存先は競合ではないため案内せず、従来どおり確認以降の経路へ進めます。
        BMSPlaylist store = playlistStoreProvider();
        if (store?.IsPlaylistMutationBusyForExplicitRequest != true) { return false; }
        await noticePresenter.BeginRequest(
            PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create([PlaylistOperationNotices.Busy()]),
            routeName).ConfigureAwait(false);
        return true;
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

    /// <summary>Pを非待機で取得します。取得できない場合は、Busyの拒否を操作の収集先へ積んでnullを返します。</summary>
    private IDisposable TryAccept(
        BMSPlaylist store,
        PlaylistWorkspaceMutationKind kind,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession)
    {
        IDisposable admission = tryBeginPlaylistMutation != null
            ? tryBeginPlaylistMutation(store, kind)
            : store.TryEnterPlaylistMutation(out IDisposable lease) ? lease : null;
        if (admission == null && store.CanAnnouncePlaylistMutationBusy)
        {
            notificationSession.Add(PlaylistOperationNotices.Rejection(kind, isBusy: true));
        }
        return admission;
    }

    private async Task RemoveTablesAsync(IEnumerable<BMSTable> tables, Action applySelectionBeforeMutation = null)
    {
        BMSPlaylist store = GetPlaylistStore();
        // 通知の収集先はPより外側に置き、Pを解放した後に表示を依頼します。表示の失敗は削除の結果へ加えません。
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = store.OperationNotificationOwner.BeginSession();
        try
        {
            await RemoveTablesAdmittedAsync(store, tables, applySelectionBeforeMutation, notificationSession).ConfigureAwait(false);
        }
        finally
        {
            await noticePresenter.BeginRequest(notificationSession.TakeReceipt(), "playlist remove custom folder notification").ConfigureAwait(false);
        }
    }

    private async Task RemoveTablesAdmittedAsync(
        BMSPlaylist store,
        IEnumerable<BMSTable> tables,
        Action applySelectionBeforeMutation,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession)
    {
        using IDisposable admission = TryAccept(store, PlaylistWorkspaceMutationKind.RemoveTable, notificationSession);
        if (admission == null) { return; }
        using LibraryFileMutationCapability authority = store.CreatePlaylistMutationCapability(admission);
        List<BMSTable> requestedTables = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (requestedTables.Count == 0) { return; }
        applySelectionBeforeMutation?.Invoke();
        List<BMSTable> removed = [];
        List<Exception> failures = [];
        CustomFolderOutputSettingsSnapshot settings = customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
        // 削除前の集合で出力先が他の表と重なっていた表は、出力状態だけを消します。集合は一度だけ求めて全表へ渡します。
        IReadOnlySet<BMSTable> collidingBefore = settings.OperationModeLR2DB ? store.FindCollidingOutputTables(settings) : new HashSet<BMSTable>();
        List<BMSTable> prepared = [];
        foreach (BMSTable requested in requestedTables)
        {
            BMSTable table = store.ResolveActivePlaylistTableForMutation(requested);
            if (table == null)
            {
                notificationSession.Add(PlaylistOperationNotices.Rejection(
                    PlaylistWorkspaceMutationKind.RemoveTable, isBusy: false, isStale: true));
                continue;
            }
            try
            {
                await Task.Run(() => PrepareTableRemoval(store, table, settings, authority, collidingBefore)).ConfigureAwait(false);
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
            try { PublishRemovedTable(table, settings, notificationSession, collidingBefore); }
            catch (Exception failure) { failures.Add(failure); }
        }
        try { await store.BmtOutput.RemoveTablesAsync(removed, "playlist_remove_tables", authority).ConfigureAwait(false); }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            SummaryRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures).Flatten(); }
    }

    private static void PrepareTableRemoval(BMSPlaylist playlistStore, BMSTable table,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability, IReadOnlySet<BMSTable> collidingBefore)
    {
        if (settings.OperationModeLR2DB && !string.IsNullOrWhiteSpace(table.Output_dir))
        {
            playlistStore.RemoveCustomFolder(table, settings, capability, collidingBefore);
        }
    }

    private void PublishRemovedTable(
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession,
        IReadOnlySet<BMSTable> collidingBefore)
    {
        // 削除前に出力先が他の表と重なっていた表の登録は、共有している可能性があるため残します。
        if (settings.OperationModeLR2DB && table.is_root_folder && !string.IsNullOrWhiteSpace(table.Output_dir) && !collidingBefore.Contains(table))
        {
            Lr2ConfigOwner owner = lr2ConfigProvider() ?? throw new InvalidOperationException("LR2 config owner is not configured.");
            string removed = ResolveCustomFolderOutputDirectory(table, settings, notificationSession);
            owner.SaveRoots(new Lr2RootChange([removed], []));
        }
    }

    private async Task RemoveFolderAsync(BMSPlaylist playlistStore, BMSTable table, string folderName,
        LibraryFileMutationCapability capability,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession)
    {
        if (!CanRemoveFolder(table, notificationSession)) { return; }
        BMSTable activeTable = playlistStore.ResolveActivePlaylistTableForMutation(table);
        if (activeTable == null
            || !playlistStore.ContainsPlaylistFolderForMutation(activeTable, folderName))
        {
            notificationSession.Add(PlaylistOperationNotices.Rejection(
                PlaylistWorkspaceMutationKind.RemoveFolder, isBusy: false, isStale: true));
            return;
        }
        try
        {
            if (!CanRemoveFolder(activeTable, notificationSession)
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
    }

    private static bool CanRemoveFolder(
        BMSTable table,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession)
    {
        if (!table.is_external_sync)
        {
            return true;
        }

        notificationSession.Add(PlaylistOperationNotices.Rejection(PlaylistWorkspaceMutationKind.RemoveFolder));
        return false;
    }

    /// <summary>出力先を解決します。出力先が不正な場合は、警告を操作の収集先へ積んでから例外を再送出します。</summary>
    private static string ResolveCustomFolderOutputDirectory(
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession)
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
            notificationSession.Add(PlaylistOperationNotices.InvalidOutputDirectory());
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
