using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// ダイアログ窓口を宣言しないテストが、ViewModelの必須の窓口へ渡す「未宣言の要求用の窓口」を解決します。
/// </summary>
/// <remarks>
/// 共有ホストの観測境界が有効なmoduleでは、要求を項目 <c>D</c> として記録する <see cref="UndeclaredUiDialogService"/> を返します。
/// 境界が無効なmodule（Core）では記録が消費されず未宣言の要求が黙って通るため、全メンバーで例外を投げる <see cref="TestUiDialogService"/> を返します。
/// ただしCoreには、窓口の例外を捕捉する製品の経路があり、その経路では未宣言の要求への到達が隠れうります。到達を表明したいケースは、記録用の窓口を明示的に注入します。
/// </remarks>
internal static class UndeclaredDialogs
{
    /// <summary>module の観測境界に応じた、未宣言の要求用の窓口を作ります。</summary>
    /// <returns>境界が有効なら <see cref="UndeclaredUiDialogService"/>、無効なら <see cref="TestUiDialogService"/>。</returns>
    internal static IUiDialogService Create() =>
        HostResidueBoundary.IsEnabled ? new UndeclaredUiDialogService() : new TestUiDialogService();
}
