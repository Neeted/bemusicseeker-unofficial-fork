namespace Parago.Windows;

public class ProgressDialogSettings
{
    public static ProgressDialogSettings WithLabelOnly = new(showSubLabel: false, showCancelButton: false, showProgressBarIndeterminate: true);

    public static ProgressDialogSettings WithSubLabel = new(showSubLabel: true, showCancelButton: false, showProgressBarIndeterminate: true);

    public static ProgressDialogSettings WithSubLabelAndCancel = new(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: true);

    public bool ShowSubLabel { get; set; }

    public bool ShowCancelButton { get; set; }

    public bool ShowProgressBarIndeterminate { get; set; }

    /// <summary>
    /// 取消の扱いを操作自身に任せるかどうかです。既定（false）では、取消を要求した後の進捗の更新を捨て、操作の例外を握りつぶし、
    /// 結果を取消へ置き換えます。trueでは、取消ボタンの押下を操作へ伝えるだけにして、取消の要求後も進捗の更新を表示し、
    /// 操作の例外と結果を取消へ置き換えずに返します。操作が自分の段階で取消の可否を決める場合（取消できない段階へ進んだ後など）に使います。
    /// </summary>
    public bool OperationOwnsCancellation { get; set; }

    public ProgressDialogSettings()
    {
        ShowSubLabel = false;
        ShowCancelButton = false;
        ShowProgressBarIndeterminate = true;
    }

    public ProgressDialogSettings(bool showSubLabel, bool showCancelButton, bool showProgressBarIndeterminate)
    {
        ShowSubLabel = showSubLabel;
        ShowCancelButton = showCancelButton;
        ShowProgressBarIndeterminate = showProgressBarIndeterminate;
    }
}
