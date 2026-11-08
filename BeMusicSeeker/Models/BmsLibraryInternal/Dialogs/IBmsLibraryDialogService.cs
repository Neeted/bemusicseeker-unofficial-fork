namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ライブラリ処理の通知・確認の窓口です。
/// 即時に表示する実装は <c>Views</c> にあり、構成がライブラリへ注入します。モデルは表示の実装を持ちません。
/// モデル内の境界の実装は、通知を溜めて既定結果を返す場合があります。
/// </summary>
internal interface IBmsLibraryDialogService
{
    /// <summary>
    /// 通知または確認を依頼し、その結果を返します。
    /// 結果の意味は実装が決めます。即時に表示する実装は、利用者の選択を返します。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">表示する題名。</param>
    /// <param name="button">表示するボタンの構成。</param>
    /// <param name="icon">表示するアイコン。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <returns>実装が返す結果。</returns>
    UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None);
}
