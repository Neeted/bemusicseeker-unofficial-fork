using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト側（P）の操作が集めた通知を、操作の受付（P と、取得していれば L）を解放した後に、
/// 見出し・重要度ごとに 1 件へまとめて表示するよう依頼します。表示の完了は待ちません。
/// </summary>
/// <remarks>
/// 依頼は UI の文脈の外で行うため、UI スレッドが表示の完了を同期で待つことも、同期のモーダルになることもありません。
/// 表示するかどうかの判定と最初の組の依頼は <see cref="Request"/> の中で行い、続きの組だけが表示の完了後に続きます。
/// 終了の要求後は表示せず診断へ残し、表示の失敗は診断へ残すだけで操作の結果を変えません。
/// 見出しと本文は依頼の時点の言語で整形し、表示窓口の解決に任せて Owner は指定しません。
/// まとめ方と表示の順は、ライブラリ側の操作通知（<see cref="FileDbMutationReport.ShowOperationMessagesAsync"/>）と同じ規則です。
/// </remarks>
internal sealed class PlaylistOperationNoticePresenter
{
    private readonly IUiDialogService dialogs;

    private readonly Func<bool> isShutdownRequested;

    private long requestCount;

    /// <summary>
    /// 表示を依頼する対象があった要求の累計数です。終了の要求で表示しなかった要求も数えます。
    /// 明示シナリオが、操作が通知を依頼しなかったことを観測するために使います。
    /// </summary>
    internal long RequestCount => Interlocked.Read(ref requestCount);

    /// <summary>表示窓口と、終了の要求を示す指標を接続します。</summary>
    /// <param name="dialogs">通知を表示する窓口。プレイリスト作業領域に注入された窓口です。</param>
    /// <param name="isShutdownRequested">依頼の直前に評価する、終了の要求の判定。</param>
    internal PlaylistOperationNoticePresenter(IUiDialogService dialogs, Func<bool> isShutdownRequested)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
    }

    /// <summary>
    /// 領収書の通知を、受付の解放後に表示するよう依頼し、表示の完了は待たずに戻ります。
    /// 終了の判定と最初の組の依頼はこの呼出しの中で行うため、呼出し元は UI の文脈の外から呼びます。
    /// 通知が空で、続きの表示もなければ何もしません。
    /// </summary>
    /// <param name="receipt">操作が集めた通知の領収書。</param>
    /// <param name="routeName">診断に残す経路名。</param>
    /// <param name="createTrailing">通知の組の表示がすべて終わった後に続けて表示する要求。省略時は続けません。</param>
    internal void Request(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName,
        Func<UiMessageRequest> createTrailing = null)
    {
        try
        {
            bool hasNotifications = receipt is { IsEmpty: false };
            if (!hasNotifications && createTrailing == null)
            {
                return;
            }
            Interlocked.Increment(ref requestCount);
            if (isShutdownRequested())
            {
                NLogWrapper.FileLogger?.Warn(
                    "playlist_operation_notice_skipped reason=shutdown route=" + routeName
                    + " count=" + (hasNotifications ? receipt.Notifications.Count : 0));
                return;
            }
            List<BMSLibrary.OperationDialogMessage> messages = hasNotifications ? ToMessages(receipt, routeName) : [];
            PresentAsync(messages, createTrailing, routeName).ObserveFault(routeName);
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    /// <summary>
    /// <see cref="Request"/> を、UI の文脈の継続から呼ばれても依頼が同期のモーダルにならないよう、スレッドプールで行います。
    /// 戻る Task は、終了の判定と最初の組の依頼が窓口へ渡るまでで完了し、表示の完了と続きの組は待ちません。
    /// </summary>
    internal Task BeginRequest(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName,
        Func<UiMessageRequest> createTrailing = null)
    {
        if (receipt is not { IsEmpty: false } && createTrailing == null)
        {
            return Task.CompletedTask;
        }
        try
        {
            return Task.Run(() => Request(receipt, routeName, createTrailing));
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 呼出し元が戻った後に、<see cref="BeginRequest"/> の依頼を行います。受付を持たない同期の入口や、
    /// 呼出し元が依頼の完了を待たない経路で使います。
    /// </summary>
    internal void RequestInBackground(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName,
        Func<UiMessageRequest> createTrailing = null)
    {
        BeginRequest(receipt, routeName, createTrailing).ObserveFault(routeName);
    }

    private static List<BMSLibrary.OperationDialogMessage> ToMessages(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName)
    {
        List<BMSLibrary.OperationDialogMessage> messages = [];
        foreach (PlaylistOperationNotificationOwner.OperationNotification notification in receipt.Notifications)
        {
            try
            {
                // 本文と見出しは、確定時ではなく依頼の時点の言語で整形します。
                messages.Add(new BMSLibrary.OperationDialogMessage(
                    notification.FormatMessage(),
                    notification.FormatCaption(),
                    UiDialogButton.OK,
                    notification.Severity switch
                    {
                        PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information => UiDialogIcon.Asterisk,
                        PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning => UiDialogIcon.Exclamation,
                        PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error => UiDialogIcon.Hand,
                        _ => UiDialogIcon.None,
                    },
                    UiDialogDefaultResult.OK));
            }
            catch (Exception exception)
            {
                // 整形の失敗も表示の失敗として扱い、残りの通知の表示は続けます。
                NLogWrapper.FileLogger?.Warn(exception, "playlist_operation_notice_format_failed route=" + routeName);
            }
        }
        return messages;
    }

    private async Task PresentAsync(
        List<BMSLibrary.OperationDialogMessage> messages,
        Func<UiMessageRequest> createTrailing,
        string routeName)
    {
        if (messages.Count > 0)
        {
            await FileDbMutationReport.ShowOperationMessagesAsync(dialogs, messages).ConfigureAwait(false);
        }
        if (createTrailing == null)
        {
            return;
        }
        try
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(createTrailing()).ConfigureAwait(false);
            if (result == null || result.Status is not (UiDialogStatus.Accepted
                or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
            {
                throw new InvalidOperationException(
                    routeName + " was not displayed: " + result?.Status, result?.Exception);
            }
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }
}

/// <summary>プレイリスト側の操作が表示する通知の事実を、資源名と重要度から作ります。</summary>
internal static class PlaylistOperationNotices
{
    /// <summary>操作を実行しなかった理由の通知です。Busy・対象消失・種類ごとの文言は、見出し <c>Error</c> の Error として扱います。</summary>
    internal static PlaylistOperationNotificationOwner.OperationNotification Rejection(
        PlaylistWorkspaceMutationKind kind,
        bool isBusy = false,
        bool isStale = false)
    {
        string resourceName = isBusy
            ? nameof(Resources.Warn_PlaylistMutationBusy)
            : isStale
                ? nameof(Resources.Warn_PlaylistMutationStale)
                : kind switch
                {
                    PlaylistWorkspaceMutationKind.RenameFolder => nameof(Resources.Msg_failed_rename_playlist_folder),
                    PlaylistWorkspaceMutationKind.RemoveFolder => nameof(Resources.Msg_failed_remove_playlist_folder),
                    PlaylistWorkspaceMutationKind.CreateFolder => nameof(Resources.Msg_failed_create_playlist_folder),
                    PlaylistWorkspaceMutationKind.AddEntries => nameof(Resources.Msg_failed_add_playlist_entry),
                    PlaylistWorkspaceMutationKind.RemoveEntries => nameof(Resources.Msg_failed_remove_playlist_entry),
                    PlaylistWorkspaceMutationKind.Reload => nameof(Resources.Msg_failed_load_playlist),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
                };
        return Error(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(resourceName));
    }

    /// <summary>出力先が不正なため処理できなかったことの警告です。</summary>
    internal static PlaylistOperationNotificationOwner.OperationNotification InvalidOutputDirectory()
        => new(
            PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Resources.Warn_CustomFolderOutputDirInvalid)),
            nameof(Resources.MessageBoxTitle_Warning),
            PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning);

    /// <summary>取込みの登録は成功した後の、要約の更新の失敗を知らせる警告です。例外の本文は作成時のままです。</summary>
    internal static PlaylistOperationNotificationOwner.OperationNotification SummaryRefreshFailure(Exception exception)
        => new(
            PlaylistOperationNotificationOwner.OperationNotificationText.FromResourceWithDetail(
                nameof(Resources.Msg_error_unexpected), exception?.Message, detailSeparatorLineBreaks: 1),
            nameof(Resources.Warning),
            PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning);

    /// <summary>外部同期に失敗したことの通知です。例外の本文が空でなければ、資源の文の次の行へ続けます。</summary>
    internal static PlaylistOperationNotificationOwner.OperationNotification ExternalSyncFailure(Exception exception)
        => Error(string.IsNullOrWhiteSpace(exception?.Message)
            ? PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Resources.Msg_failed_load_playlist))
            : PlaylistOperationNotificationOwner.OperationNotificationText.FromResourceWithDetail(
                nameof(Resources.Msg_failed_load_playlist), exception.Message, detailSeparatorLineBreaks: 1));

    /// <summary>プロパティの入力不備の通知です。</summary>
    internal static PlaylistOperationNotificationOwner.OperationNotification ValidationError(PlaylistPropertyValidationError error)
    {
        string resourceName = error switch
        {
            PlaylistPropertyValidationError.OutputDirectoryChangedByPlaylistName => nameof(Resources.Error_OutputFolderNameEmptyOrDuplicateChangePlaylist),
            PlaylistPropertyValidationError.InvalidOutputDirectory => nameof(Resources.Error_OutputFolderNameEmptyOrDuplicateCheckInput),
            PlaylistPropertyValidationError.OutputDirectoryNameMustBeSingleName => nameof(Resources.Error_OutputFolderNameMustBeSingleName),
            PlaylistPropertyValidationError.InvalidPageUri => nameof(Resources.Error_InvalidPageUriAbsoluteRequired),
            PlaylistPropertyValidationError.InvalidHeaderUri => nameof(Resources.Error_InvalidHeaderUri),
            PlaylistPropertyValidationError.InvalidDataUri => nameof(Resources.Error_InvalidDataUri),
            PlaylistPropertyValidationError.InvalidExternalSyncUris => nameof(Resources.Error_InvalidPageOrHeaderUri),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
        };
        return Error(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(resourceName));
    }

    /// <summary>
    /// 通知を、現在の操作の収集先があればそこへ積み、なければ待たずに表示を依頼します。
    /// 収集先へ積んだ通知は、その操作の最も外側が受付を解放した後にまとめて依頼します。
    /// 通知の失敗は診断へ残すだけで、呼出し元の処理へ伝えません。
    /// </summary>
    /// <param name="store">現在の操作の収集先を持つプレイリストの保存先。null の場合は収集先を持たないものとして扱います。</param>
    /// <param name="presenter">収集先がない場合に表示を依頼する表示役。</param>
    /// <param name="notification">知らせる通知の事実。</param>
    /// <param name="routeName">診断に残す経路名。</param>
    internal static void Notify(
        BMSPlaylist store,
        PlaylistOperationNoticePresenter presenter,
        PlaylistOperationNotificationOwner.OperationNotification notification,
        string routeName)
    {
        try
        {
            if (store?.OperationNotificationOwner.TryAddToCurrentSession(notification) == true)
            {
                return;
            }
            if (presenter == null)
            {
                LogPresenterNotConnected(routeName, 1);
                return;
            }
            presenter.RequestInBackground(
                PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create([notification]), routeName);
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    /// <summary>表示役が未接続のため表示できなかった通知を、診断へ残します。表示役の未接続を示す破棄ログの唯一の定義です。</summary>
    internal static void LogPresenterNotConnected(string routeName, int count)
    {
        if (count <= 0)
        {
            return;
        }
        NLogWrapper.FileLogger?.Warn(
            "playlist_operation_notice_discarded reason=presenter_not_connected route=" + routeName + " count=" + count);
    }

    private static PlaylistOperationNotificationOwner.OperationNotification Error(
        PlaylistOperationNotificationOwner.OperationNotificationText text)
        => new(text, nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error);
}
