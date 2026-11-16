namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 設定画面の表示要求と終了要求をシェルへ接続します。
/// </summary>
internal interface ISettingDialogPresentationPort
{
    /// <summary>設定画面を表示し、表示中なら既存画面へ戻します。</summary>
    /// <param name="deferPresentation">呼出元の後処理をモーダル表示で待たせない場合は、表示をUIキューへ送ります。</param>
    void OpenSettingsDialog(bool deferPresentation = false);

    void OpenInitialSetupLanguageDialog();

    /// <summary>設定画面の終了を同期の要求として出します。完了は待ちません。</summary>
    void CloseSettingsDialog();

    void RefreshAppearanceSelection();
}
