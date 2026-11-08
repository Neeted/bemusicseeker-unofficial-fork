using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// モデルが持つ同期の窓口 <see cref="IBmsLibraryDialogService"/> を、注入された <see cref="IUiDialogService"/> の表示へ接続します。
/// Models は Views に依存しないため、実装はこの層に置き、構成がライブラリへ注入します。
/// </summary>
internal sealed class BmsLibraryDialogService : IBmsLibraryDialogService
{
    private readonly IUiDialogService dialogService;

    /// <summary>表示を委譲する先を受け取ります。</summary>
    /// <param name="dialogService">構成が所有する UI ダイアログ窓口。</param>
    internal BmsLibraryDialogService(IUiDialogService dialogService)
    {
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    /// <summary>
    /// 通知または確認を表示し、利用者の応答を同期で待って返します。
    /// 表示できなかった場合や表示中に失敗した場合は、既定結果へ置き換えず例外を投げます。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">表示する題名。</param>
    /// <param name="button">表示するボタンの構成。OK だけ通知として、それ以外は確認として表示します。</param>
    /// <param name="icon">表示するアイコン。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <returns>利用者が選んだ結果。</returns>
    public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
    {
        UiDialogResult result = button == UiDialogButton.OK
            ? dialogService
                .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, button, icon, defaultResult))
                .GetAwaiter()
                .GetResult()
            : dialogService
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
