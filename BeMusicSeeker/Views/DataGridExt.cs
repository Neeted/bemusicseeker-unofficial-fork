using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BeMusicSeeker.Views;

public static class DataGridExt
{
	/// <summary>
	/// 現在選択されている行コンテナを返します。仮想化で未実体化の場合は再取得を試みます。
	/// </summary>
	/// <param name="grid">対象のDataGrid。</param>
	/// <returns>選択行のDataGridRow。取得できない場合はnull。</returns>
	public static DataGridRow GetSelectedRow(this DataGrid grid)
	{
		TryGetSelectedRowRealized(grid, out var selectedRow);
		return selectedRow;
	}

	/// <summary>
	/// 現在選択されている行コンテナを取得します。必要に応じて <see cref="DataGrid.ScrollIntoView(object)"/> を呼び出し、
	/// 仮想化で未実体化の行コンテナを生成して再取得します。
	/// </summary>
	/// <param name="grid">対象のDataGrid。</param>
	/// <param name="selectedRow">取得した選択行コンテナ。</param>
	/// <returns>取得に成功した場合はtrue。</returns>
	public static bool TryGetSelectedRowRealized(this DataGrid grid, out DataGridRow selectedRow)
	{
		selectedRow = null;
		if (grid?.SelectedItem == null)
		{
			return false;
		}
		return TryGetRowRealized(grid, grid.SelectedItem, out selectedRow);
	}

	/// <summary>
	/// 指定項目に対応する行コンテナを取得します。仮想化で未実体化の場合は再取得を試みます。
	/// </summary>
	/// <param name="grid">対象のDataGrid。</param>
	/// <param name="item">行コンテナ取得対象の項目。</param>
	/// <param name="row">取得した行コンテナ。</param>
	/// <returns>取得に成功した場合はtrue。</returns>
	public static bool TryGetRowRealized(this DataGrid grid, object item, out DataGridRow row)
	{
		row = null;
		if (grid == null || item == null)
		{
			return false;
		}
		row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
		if (row != null)
		{
			return true;
		}
		try
		{
			grid.ScrollIntoView(item);
			grid.UpdateLayout();
			row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
		}
		catch
		{
			row = null;
		}
		return row != null;
	}

	public static IEnumerable<DataGridRow> GetSelectedRows(this DataGrid grid)
	{
		foreach (object selectedItem in grid.SelectedItems)
		{
			DataGridRow selectedRow = grid.ItemContainerGenerator.ContainerFromItem(selectedItem) as DataGridRow;
			if (selectedRow != null)
			{
				yield return selectedRow;
			}
		}
	}

	public static DataGridCell GetCell(this DataGrid grid, DataGridRow row, int column)
	{
		if (row != null)
		{
			DataGridCellsPresenter visualChild = WPFUtil.GetVisualChild<DataGridCellsPresenter>(row);
			if (visualChild == null)
			{
				grid.ScrollIntoView(row, grid.Columns[column]);
				visualChild = WPFUtil.GetVisualChild<DataGridCellsPresenter>(row);
			}
			return (DataGridCell)visualChild.ItemContainerGenerator.ContainerFromIndex(column);
		}
		return null;
	}

	public static DataGridCell GetCell(this DataGrid grid, int row, int column)
	{
		DataGridRow row2 = grid.GetRow(row);
		return grid.GetCell(row2, column);
	}

	public static DataGridRow GetRow(this DataGrid grid, int index)
	{
		DataGridRow dataGridRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
		if (dataGridRow == null)
		{
			grid.UpdateLayout();
			grid.ScrollIntoView(grid.Items[index]);
			dataGridRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
		}
		return dataGridRow;
	}
}
