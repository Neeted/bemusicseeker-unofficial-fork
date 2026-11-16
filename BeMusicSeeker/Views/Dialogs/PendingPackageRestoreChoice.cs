namespace BeMusicSeeker.Views.Dialogs;

/// <summary>一回の保留復元失敗でのみ使う明示応答です。初期化や終了の長期状態として保持しません。</summary>
internal enum PendingPackageRestoreChoice
{
    /// <summary>明示選択がありません。閉鎖・Escは全登録を保持して今回の初期化を失敗にします。</summary>
    None,
    /// <summary>保留DB・入力全体を新たに取得して復元します。</summary>
    Retry,
    /// <summary>全保留登録だけを一括削除し、成功後に通常初期化へ進みます。</summary>
    Reset,
    /// <summary>親受付の解放後に既存の終了経路へ渡します。</summary>
    Exit
}
