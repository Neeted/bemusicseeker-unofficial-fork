using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// ライブラリ用の窓口を明示しないテストの既定です。モデルは既定の実装を持たないため、
/// 呼ばれたら題名・本文・ボタンを含む例外にして予期しない通知を見えるようにします。
/// </summary>
internal sealed class UnexpectedBmsLibraryDialogService : IBmsLibraryDialogService
{
    /// <inheritdoc />
    public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        => throw new InvalidOperationException("予期しないライブラリの通知です。題名: " + caption + " / 文言: " + messageBoxText + " / ボタン: " + button);
}
