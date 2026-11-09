using System;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 窓口が返した表示結果を検査する薄い入口です。
/// 表示失敗を既定ボタンに丸めず、呼び出し側へ例外として返します。
/// </summary>
internal static class UiDialogRoute
{
    /// <summary>
    /// dialog が表示されなかった状態を例外として扱います。確認失敗をキャンセルとして隠さないための境界です。
    /// </summary>
    /// <param name="result">coordinator から返された dialog 結果。</param>
    /// <param name="routeName">例外 message に含める route 名。</param>
    /// <exception cref="InvalidOperationException">dialog が表示されなかった、または表示中に失敗した場合。</exception>
    internal static void ThrowIfNotShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.IsShown)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception);
    }
}
