using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ライブラリ処理の通知・確認の窓口です。
/// 表示の実装は <c>Views</c> にあり、構成がライブラリへ注入します。モデルは表示の実装を持ちません。
/// 利用者の応答を待つ確認は、受付の前に行う同期の <see cref="Confirm"/> だけです。
/// OK だけの通知は、操作の所有者が持つ収集先 <see cref="LibraryOperationNotices"/> へ加えて所有者が受付の解放後に表示するか、
/// モデルが根の背景処理が待たない <see cref="Post"/> で依頼します。この窓口は OK だけの通知を同期に表示しません。
/// </summary>
internal interface IBmsLibraryDialogService
{
    /// <summary>
    /// 利用者の応答が必要な確認を表示し、利用者の選択を同期で返します。受付の前の確認だけが使います。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">表示する題名。</param>
    /// <param name="button">表示するボタンの構成。OK だけは確認ではなく通知なので受け付けません。</param>
    /// <param name="icon">表示するアイコン。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <returns>利用者が選んだ結果。</returns>
    UiDialogDefaultResult Confirm(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None);

    /// <summary>
    /// OK だけの通知を、表示の開始も完了も待たずに依頼します。
    /// 表示する実装は、見出し・重要度ごとに一件へまとめ、組の間は先行の表示の完了を待って順に表示します。
    /// 表示できなかった通知は呼出し元へ例外で伝えず、診断だけに残します。
    /// </summary>
    /// <param name="messages">表示する通知。空では何も表示しません。</param>
    /// <param name="shouldSuppress">表示の開始の時点で真なら、通知を表示せず診断へ残す判定。終了中や失効した世代の通知を、依頼から表示までの間にも止めるために使います。</param>
    void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress);
}
