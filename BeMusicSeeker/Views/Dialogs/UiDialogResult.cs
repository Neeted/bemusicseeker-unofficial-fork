using System;
using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// UI dialog の表示結果を表します。表示できなかった状態をユーザーの拒否やキャンセルに丸めないために使います。
/// </summary>
internal sealed class UiDialogResult
{
    private UiDialogResult(UiDialogStatus status, MessageBoxResult messageBoxResult, Exception exception)
    {
        Status = status;
        MessageBoxResult = messageBoxResult;
        Exception = exception;
    }

    /// <summary>
    /// dialog route 全体としての結果です。
    /// </summary>
    internal UiDialogStatus Status { get; }

    /// <summary>
    /// message box 表示部品から返された元の結果です。表示されなかった場合は <see cref="System.Windows.MessageBoxResult.None"/> です。
    /// </summary>
    internal MessageBoxResult MessageBoxResult { get; }

    /// <summary>
    /// 表示失敗の原因例外です。ユーザー操作による終了では設定されません。
    /// </summary>
    internal Exception Exception { get; }

    /// <summary>
    /// 処理を続行してよい肯定応答かどうかを返します。
    /// </summary>
    internal bool IsAccepted => Status == UiDialogStatus.Accepted;

    /// <summary>
    /// dialog を閉じた場合も含め、肯定結果として扱えるかどうかを返します。
    /// </summary>
    internal bool IsPositive => Status == UiDialogStatus.Accepted
        || Status == UiDialogStatus.ClosedByUser
            && MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes;

    /// <summary>
    /// WPF message box の戻り値を coordinator の結果へ変換します。
    /// </summary>
    /// <param name="result">WPF message box の戻り値。</param>
    /// <returns>coordinator の結果。</returns>
    internal static UiDialogResult FromMessageBoxResult(MessageBoxResult result)
    {
        UiDialogStatus status = result switch
        {
            MessageBoxResult.OK or MessageBoxResult.Yes => UiDialogStatus.Accepted,
            MessageBoxResult.No => UiDialogStatus.Rejected,
            MessageBoxResult.Cancel => UiDialogStatus.CancelledByUser,
            MessageBoxResult.None => UiDialogStatus.ClosedByUser,
            _ => UiDialogStatus.ClosedByUser,
        };
        return new UiDialogResult(status, result, null);
    }

    /// <summary>
    /// dialog を表示できなかった結果を作成します。
    /// </summary>
    /// <param name="status">表示できなかった理由。</param>
    /// <returns>表示失敗を表す結果。</returns>
    internal static UiDialogResult NotShown(UiDialogStatus status)
    {
        return new UiDialogResult(status, MessageBoxResult.None, null);
    }

    /// <summary>
    /// ユーザーが window close で閉じた結果を作成します。
    /// </summary>
    /// <param name="normalizedResult">legacy message box 互換のために表示部品が保持する既定結果。</param>
    /// <returns>window close を表す結果。</returns>
    internal static UiDialogResult ClosedByUser(MessageBoxResult normalizedResult)
    {
        return new UiDialogResult(UiDialogStatus.ClosedByUser, normalizedResult, null);
    }

    /// <summary>
    /// dialog 表示中の例外を結果として保持します。
    /// </summary>
    /// <param name="exception">表示失敗の原因例外。</param>
    /// <returns>失敗を表す結果。</returns>
    internal static UiDialogResult Failed(Exception exception)
    {
        return new UiDialogResult(UiDialogStatus.Failed, MessageBoxResult.None, exception);
    }
}
