namespace BeMusicSeeker.Views;

/// <summary>
/// 連続した行を一つのグループとして扱う一覧の契約です。<see cref="CustomTableView.ItemsSource"/> がこの契約を実装した表だけが、
/// グループ単位の交互背景と、グループの全行にまたがる列（<see cref="CustomTableColumn.MergesRowGroup"/>）、無効セルの描画を行います。
/// 実装しない表（既存の全用途）の描画・操作・UI Automationは変わりません。
/// </summary>
/// <remarks>
/// 行の集合とグループの境界を同じ一覧が持つため、行集合の差替えでグループの境界だけが古いまま残ることがありません。
/// 返値が不整合（連続でない、行を含まない、行を重複して数える）な場合は契約外で、表は検査も保証もしません。
/// 結合列の値・チェック状態・ツールチップ・操作可否は、グループ内の全行で一致させることを行の供給側の責任とします。
/// </remarks>
internal interface ICustomTableRowGroupSource
{
    /// <summary>
    /// 指定した行が属するグループを返します。表は描画の単位ごとに、表示範囲に現れるグループの数だけ問い合わせ、
    /// グループの範囲を一行ずつ走査しません。実装も、グループの大きさに比例しない処理量で返します。
    /// </summary>
    /// <param name="rowIndex">一覧の範囲内の行番号。</param>
    CustomTableRowGroup GetRowGroup(int rowIndex);
}

/// <summary>連続した行のグループです。</summary>
/// <param name="FirstRowIndex">グループの先頭行番号。</param>
/// <param name="RowCount">グループの行数。</param>
/// <param name="Ordinal">一覧の先頭から数えたグループの序数（0始まり）。交互背景は、この序数の偶奇で決めます。</param>
internal readonly record struct CustomTableRowGroup(int FirstRowIndex, int RowCount, int Ordinal)
{
    /// <summary>グループの次の行番号です（グループに含まれない最初の行）。</summary>
    internal int EndRowIndex => FirstRowIndex + RowCount;
}
