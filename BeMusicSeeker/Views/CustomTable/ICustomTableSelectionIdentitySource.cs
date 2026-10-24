using System;

namespace BeMusicSeeker.Views;

/// <summary>差分適用前後の選択識別を、行の実体化や全件列挙なしで照合する一覧の契約です。</summary>
internal interface ICustomTableSelectionIdentitySource
{
    /// <summary>表示順序・所属を変更する前に、選択と主行・基点の捕捉を要求します。</summary>
    event EventHandler SelectionUpdateStarting;

    /// <summary>差分と順序通知の完了後に、捕捉した識別からの選択復元を要求します。</summary>
    event EventHandler SelectionUpdateCompleted;

    /// <summary>表示添字に対応する識別を返します。範囲外はnullで、行を実体化しません。</summary>
    object GetSelectionIdentity(int index);

    /// <summary>捕捉した識別の現在位置を返します。表示から消えた場合は-1です。</summary>
    int IndexOfSelectionIdentity(object identity);

    /// <summary>同じ通常/専用元集合の再評価かを判定し、別区分への遷移へ選択を持ち越しません。</summary>
    bool SharesSelectionIdentityScope(ICustomTableSelectionIdentitySource other);
}
