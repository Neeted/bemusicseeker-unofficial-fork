namespace BeMusicSeeker.ViewModels;

/// <summary>処理ごとの表示値と、その行にだけ適用する末尾操作を保持します。</summary>
/// <param name="Key">表示する仕事の固定識別。</param>
/// <param name="Label">処理名、状態と件数。</param>
/// <param name="Detail">実行中の対象。</param>
/// <param name="Value">処理済み数。</param>
/// <param name="Maximum">確定した総数。ゲージ用に1以上へ正規化します。</param>
/// <param name="IsIndeterminate">実行中に総数が未確定か。</param>
/// <param name="Action">その行にだけ適用する操作。</param>
/// <param name="ToolTip">表示全文に加えて提示する診断理由。</param>
/// <param name="ParentKey">所属する親行。独立処理では空文字。</param>
/// <param name="HasGauge">実行中または件数付き進捗としてゲージを表示するか。</param>
public sealed record OperationProgressRow(
    string Key,
    string Label,
    string Detail,
    double Value = 0,
    double Maximum = 1,
    bool IsIndeterminate = false,
    OperationProgressAction Action = OperationProgressAction.None,
    string ToolTip = "",
    string ParentKey = "",
    bool HasGauge = true)
{
    /// <summary>親に所属する子処理かを取得します。</summary>
    public bool IsChild => !string.IsNullOrEmpty(ParentKey);

    /// <summary>省略された対象と診断理由を参照できるツールチップを取得します。</summary>
    public string FullText => Label + " " + Detail + (string.IsNullOrWhiteSpace(ToolTip) ? string.Empty : "\n" + ToolTip);

    /// <summary>末尾の取消操作を表示するかを取得します。</summary>
    public bool CanCancel => Action is OperationProgressAction.CancelInstall
        or OperationProgressAction.CancelUrlDownload or OperationProgressAction.CancelMaintenance;

}

/// <summary>表示行から既存の管理主体へ渡す操作を識別します。</summary>
public enum OperationProgressAction
{
    None,
    CancelInstall,
    CancelUrlDownload,
    CancelMaintenance
}
