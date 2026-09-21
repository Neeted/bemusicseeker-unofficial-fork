using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 設定画面の表示要求と、後続処理へ渡す表示寿命の終了をシェルへ接続します。
/// </summary>
internal interface ISettingDialogPresentationPort
{
    /// <summary>設定画面を表示し、表示中なら既存画面へ戻します。</summary>
    /// <param name="deferPresentation">呼出元の後処理をモーダル表示で待たせない場合は、表示をUIキューへ送ります。</param>
    void OpenSettingsDialog(bool deferPresentation = false);

    void OpenInitialSetupLanguageDialog();

    /// <summary>後続処理を伴わない取消などのために、設定画面の終了を要求します。</summary>
    void CloseSettingsDialog();

    /// <summary>設定画面を閉じ、モーダル表示とシェルの表示状態の後片付けが完了するまで待ちます。</summary>
    /// <returns>保存後の通知や初期化を開始してよい境界を表すタスク。</returns>
    Task CloseSettingsDialogAsync();

    void RefreshAppearanceSelection();
}
