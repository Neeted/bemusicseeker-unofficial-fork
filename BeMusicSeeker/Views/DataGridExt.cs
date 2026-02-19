using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BeMusicSeeker.Views;

public static class DataGridExt
{
	public static DataGridRow GetSelectedRow(this DataGrid grid)
	{
		return (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem);
	}

	public static IEnumerable<DataGridRow> GetSelectedRows(this DataGrid grid)
	{
		foreach (object selectedItem in grid.SelectedItems)
		{
			yield return (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(selectedItem);
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
