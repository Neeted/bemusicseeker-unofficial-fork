using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace BeMusicSeeker.Views;

/// <summary>差分適用前後の選択識別を、行の実体化や全件列挙なしで照合する一覧の契約です。</summary>
internal interface ICustomTableSelectionIdentitySource
{
    /// <summary>表示順序・所属を変更する前に選択と主行・基点の捕捉を要求します。実全体Resetの型付き通知だけは、旧行への編集値引渡しを捕捉より先に行います。</summary>
    event EventHandler SelectionUpdateStarting;

    /// <summary>差分と順序通知の完了後に、捕捉した識別からの選択復元を要求します。</summary>
    event EventHandler SelectionUpdateCompleted;

    /// <summary>表示添字に対応する識別を返します。範囲外はnullで、行を実体化しません。</summary>
    object GetSelectionIdentity(int index);

    /// <summary>捕捉した識別の現在位置を返します。表示から消えた場合は-1です。</summary>
    int IndexOfSelectionIdentity(object identity);

    /// <summary>同じ表示文脈の再評価かを判定し、別区分・別表・別folderへの遷移へ選択を持ち越しません。</summary>
    bool SharesSelectionIdentityScope(ICustomTableSelectionIdentitySource other);
}

/// <summary>全体Reset前に編集値を旧行へ一回引き渡してから、更新直前の選択捕捉を要求します。局所変更と同じ表示の再適用では使いません。</summary>
internal sealed class CustomTableSelectionResetStartingEventArgs : EventArgs
{
}

/// <summary>操作固有の選択意図を、一般の更新直前選択より優先して一回だけ適用します。</summary>
internal sealed class CustomTableSelectionUpdateEventArgs : EventArgs
{
    /// <summary>更新終端だけで使う選択集合・主行・基点を捕捉します。</summary>
    internal CustomTableSelectionUpdateEventArgs(IEnumerable<object> selected, object current, object anchor)
    {
        Selected = selected.ToArray();
        Current = current;
        Anchor = anchor;
    }

    /// <summary>更新後の表示で選ぶ識別集合です。</summary>
    internal object[] Selected { get; }
    /// <summary>主行の識別です。消失時は生存選択の表示順先頭を使います。</summary>
    internal object Current { get; }
    /// <summary>Shift基点の識別です。消失時は主行を使います。</summary>
    internal object Anchor { get; }
}

/// <summary>開始通知を含む更新を終端し、完了通知の失敗で先行の失敗を置き換えません。</summary>
internal static class CustomTableSelectionUpdate
{
    /// <summary>開始・更新・完了を一組で実行し、早期returnや開始通知の失敗でも完了を公開します。</summary>
    internal static T Run<T>(Action starting, Func<T> update, Action completed)
    {
        ExceptionDispatchInfo failure = null;
        T result = default;
        try
        {
            try
            {
                starting();
                result = update();
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }
        finally
        {
            try { completed(); }
            catch (Exception ex)
            {
                if (failure != null) { throw new AggregateException(failure.SourceException, ex); }
                throw;
            }
        }
        failure?.Throw();
        return result;
    }
}
