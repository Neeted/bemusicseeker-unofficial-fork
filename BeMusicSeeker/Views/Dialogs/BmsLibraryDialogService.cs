using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Ribbit.Logging;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// モデルが持つ窓口 <see cref="IBmsLibraryDialogService"/> を、注入された <see cref="IUiDialogService"/> の表示へ接続します。
/// 利用者の応答を待つ確認は受付の前に行う同期の <see cref="Confirm"/>、待たない通知は <see cref="Post"/> です。
/// Models は Views に依存しないため、実装はこの層に置き、構成がライブラリへ注入します。
/// </summary>
internal sealed class BmsLibraryDialogService : IBmsLibraryDialogService
{
    private readonly IUiDialogService dialogService;
    private readonly IUiScheduler uiScheduler;

    /// <summary>表示を委譲する先を受け取ります。</summary>
    /// <param name="dialogService">構成が所有する UI ダイアログ窓口。</param>
    /// <param name="uiScheduler">待たない通知の表示を、依頼したスレッドと切り離して開始する UI の予約先。</param>
    internal BmsLibraryDialogService(IUiDialogService dialogService, IUiScheduler uiScheduler)
    {
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
    }

    /// <summary>
    /// OK だけの通知を、見出し・重要度ごとに一件へまとめて待たずに表示します。
    /// 表示は UI の予約先で開始します。UI スレッドから呼ばれても、窓口の同期のモーダル経路を使いません。
    /// 予約の拒否・予約中の例外・表示の失敗は呼出し元へ伝えず、診断へ残します。別の表示手段へは切り替えません。
    /// </summary>
    /// <param name="messages">表示する通知。空では何も表示しません。</param>
    /// <param name="shouldSuppress">表示の開始の時点で真なら、表示せず診断へ残す判定。</param>
    public void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress)
    {
        if (messages == null || messages.Count == 0)
        {
            return;
        }
        BMSLibrary.OperationDialogMessage[] snapshot = [.. messages];
        try
        {
            IUiScheduledOperation operation = uiScheduler.Schedule(() => BeginShowPosted(snapshot, shouldSuppress));
            if (operation == null || !operation.IsAccepted)
            {
                LogWarn("bms_library_notice_post_rejected count=" + snapshot.Length + " reason=" + operation?.RejectionReason);
            }
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    private void BeginShowPosted(BMSLibrary.OperationDialogMessage[] messages, Func<bool> shouldSuppress)
    {
        try
        {
            if (shouldSuppress?.Invoke() == true)
            {
                LogWarn("bms_library_notice_post_suppressed count=" + messages.Length);
                return;
            }
            FileDbMutationReport.ShowOperationMessagesAsync(dialogService, messages).ObserveFault("BmsLibraryDialogService.Post");
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    private static void LogWarn(string message)
    {
        try { NLogWrapper.FileLogger?.Warn(message); }
        catch (Exception) { /* 任意診断の失敗で、通知の依頼元を変えません。 */ }
    }

    /// <summary>
    /// 確認を表示し、利用者の応答を同期で待って返します。受付の前の確認だけが使います。
    /// 表示できなかった場合や表示中に失敗した場合は、既定結果へ置き換えず例外を投げます。
    /// OK だけの通知は同期に表示せず、操作の収集先か <see cref="Post"/> を使うため受け付けません。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">表示する題名。</param>
    /// <param name="button">表示するボタンの構成。OK だけは通知なので受け付けません。</param>
    /// <param name="icon">表示するアイコン。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <returns>利用者が選んだ結果。</returns>
    public UiDialogDefaultResult Confirm(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
    {
        if (button == UiDialogButton.OK)
        {
            throw new ArgumentException("An OK-only notice is not a confirmation; add it to the operation notices or post it.", nameof(button));
        }
        UiDialogResult result = dialogService
            .ConfirmAsync(new UiConfirmationRequest(messageBoxText, caption, button, icon, defaultResult))
            .GetAwaiter()
            .GetResult();
        return ToMessageBoxResult(result, caption);
    }

    private static UiDialogDefaultResult ToMessageBoxResult(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException((routeName ?? "BMS library dialog") + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return result.DefaultResult;
        }
        if (result.Status == UiDialogStatus.Failed)
        {
            throw new InvalidOperationException((routeName ?? "BMS library dialog") + " failed.", result.Exception);
        }
        throw new InvalidOperationException((routeName ?? "BMS library dialog") + " was not shown: " + result.Status);
    }
}
