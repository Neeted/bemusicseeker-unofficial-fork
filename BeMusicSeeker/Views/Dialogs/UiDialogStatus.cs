namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// UI dialog route の結果を、ユーザー操作と表示失敗を混同しない粒度で表します。
/// </summary>
internal enum UiDialogStatus
{
    /// <summary>
    /// ユーザーが肯定応答を選択しました。
    /// </summary>
    Accepted,

    /// <summary>
    /// ユーザーが否定応答を選択しました。
    /// </summary>
    Rejected,

    /// <summary>
    /// ユーザーがキャンセル応答を選択しました。
    /// </summary>
    CancelledByUser,

    /// <summary>
    /// ユーザーが明示ボタンではなく window close で閉じました。
    /// </summary>
    ClosedByUser,

    /// <summary>
    /// 表示要求は受け付けられましたが、dialog は表示されませんでした。
    /// </summary>
    NotShown,

    /// <summary>
    /// アプリケーション終了処理中のため表示しませんでした。
    /// </summary>
    AppClosing,

    /// <summary>
    /// 通常 route で必要な owner window を解決できませんでした。
    /// </summary>
    OwnerUnavailable,

    /// <summary>
    /// UI dispatcher を利用できませんでした。
    /// </summary>
    DispatcherUnavailable,

    /// <summary>
    /// dialog 表示処理が例外で失敗しました。
    /// </summary>
    Failed
}
