using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 一つのライブラリ操作が生む、OKだけの案内を集める入れ物です。
/// 操作の所有者が作り、モデルへ明示の引数として渡します。スレッドや非同期の継続に依存せず、どのスレッド・段階・公開の時機で加えた案内も同じ列へ入ります。
/// 所有者は、モデルの操作が戻った後（操作の中の公開と延期先の排出が済んだ後）に <see cref="Snapshot"/> を取り、受付の解放後に表示します。
/// 変更権限には添えません。権限は受付の解放で無効になりますが、案内は解放後に表示するため、寿命が合わないからです。
/// </summary>
internal sealed class LibraryOperationNotices
{
    private readonly object gate = new();
    private readonly List<BMSLibrary.OperationDialogMessage> messages = [];
    private readonly HashSet<(string Text, string Caption, UiDialogButton Button, UiDialogIcon Icon, UiDialogDefaultResult DefaultResult)> keys = [];

    /// <summary>
    /// 案内を加えます。本文・見出し・ボタン・アイコン・既定値がすべて一致する案内は、最初の一件だけを残します。
    /// 件数や標本の違いは重複の判定に含めません。
    /// </summary>
    /// <param name="message">加える案内。</param>
    internal void Add(BMSLibrary.OperationDialogMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            if (keys.Add((message.MessageBoxText, message.Caption, message.Button, message.Icon, message.DefaultResult)))
            {
                messages.Add(message);
            }
        }
    }

    /// <summary>これまでに加えた案内を、加えた順に取得します。取得後の追加は、返した写しへ反映しません。</summary>
    internal IReadOnlyList<BMSLibrary.OperationDialogMessage> Snapshot()
    {
        lock (gate)
        {
            return [.. messages];
        }
    }
}
