using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

internal class DragBehavior
{
	public static readonly DependencyProperty IsEnableProperty = DependencyProperty.RegisterAttached("IsEnable", typeof(bool), typeof(DragBehavior), new PropertyMetadata(false, IsEnableChanged));

	private static readonly DependencyProperty StartPointProperty = DependencyProperty.RegisterAttached("StartPoint", typeof(Point?), typeof(DragBehavior), new PropertyMetadata(null));

	private static readonly DependencyProperty DragSourceRowProperty = DependencyProperty.RegisterAttached("DragSourceRow", typeof(DataGridRow), typeof(DragBehavior), new PropertyMetadata(null));

	private static readonly DependencyProperty DragAdornerProperty = DependencyProperty.RegisterAttached("DragAdorner", typeof(DragAdorner), typeof(DragBehavior), new PropertyMetadata(null));

	[AttachedPropertyBrowsableForType(typeof(UIElement))]
	public static bool GetIsEnable(DependencyObject obj)
	{
		return (bool)obj.GetValue(IsEnableProperty);
	}

	[AttachedPropertyBrowsableForType(typeof(UIElement))]
	public static void SetIsEnable(DependencyObject obj, bool value)
	{
		obj.SetValue(IsEnableProperty, value);
	}

	public static void IsEnableChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
	{
		if (sender is UIElement uIElement)
		{
			if ((bool)e.NewValue)
			{
				uIElement.PreviewMouseDown += OnPreviewMouseDown;
				uIElement.PreviewMouseUp += OnPreviewMouseUp;
				uIElement.MouseMove += OnMouseMove;
				uIElement.QueryContinueDrag += OnQueryContinueDrag;
			}
			else
			{
				uIElement.PreviewMouseDown -= OnPreviewMouseDown;
				uIElement.PreviewMouseUp -= OnPreviewMouseUp;
				uIElement.MouseMove -= OnMouseMove;
				uIElement.QueryContinueDrag -= OnQueryContinueDrag;
			}
		}
	}

	public static DragAdorner GetDragAdorner(DependencyObject obj)
	{
		return (DragAdorner)obj.GetValue(DragAdornerProperty);
	}

	public static void SetDragAdorner(DependencyObject obj, DragAdorner value)
	{
		obj.SetValue(DragAdornerProperty, value);
	}

	private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		UIElement uIElement = sender as UIElement;
		DataGridRow dataGridRow = ResolveDataGridRow(sender, e.OriginalSource);
		if (uIElement == null || dataGridRow == null)
		{
			return;
		}
		bool handled = true;
		DataGrid dataGrid = ResolveDataGrid(sender, dataGridRow);
		if (dataGrid != null)
		{
			if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
			{
				handled = false;
				dataGrid.SelectionMode = DataGridSelectionMode.Extended;
			}
			else if (dataGrid.SelectedItems.Count <= 1 || !dataGrid.GetSelectedRows().Contains(dataGridRow))
			{
				handled = false;
				dataGrid.SelectionMode = DataGridSelectionMode.Single;
			}
			else if (e.ButtonState == MouseButtonState.Pressed && e.ChangedButton == MouseButton.Right)
			{
				handled = false;
			}
			NLogWrapper.DebuggerLogger?.Trace(dataGrid.SelectionMode.ToString());
		}
		NLogWrapper.DebuggerLogger?.Trace(handled.ToString());
		e.Handled = handled;
		uIElement.SetValue(StartPointProperty, e.GetPosition(uIElement));
		uIElement.SetValue(DragSourceRowProperty, dataGridRow);
	}

	private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		UIElement obj = sender as UIElement;
		DataGridRow dataGridRow = ResolveDataGridRow(sender, e.OriginalSource) ?? obj?.GetValue(DragSourceRowProperty) as DataGridRow;
		if (dataGridRow != null)
		{
			DataGrid dataGrid = ResolveDataGrid(sender, dataGridRow);
			if (dataGrid != null && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift) && !Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl) && dataGrid.SelectedItems.Count > 1 && dataGrid.GetSelectedRows().Contains(dataGridRow) && e.ChangedButton != MouseButton.Right)
			{
				dataGrid.UnselectAllCells();
				dataGrid.UnselectAll();
				dataGrid.SelectedItem = dataGridRow.DataContext;
			}
		}
		obj?.SetValue(StartPointProperty, null);
		obj?.SetValue(DragSourceRowProperty, null);
	}

	private static void OnMouseMove(object sender, MouseEventArgs e)
	{
		UIElement uIElement = sender as UIElement;
		if (uIElement == null)
		{
			return;
		}
		Point position = e.GetPosition(uIElement);
		object startPoint = uIElement.GetValue(StartPointProperty);
		if (startPoint == null)
		{
			return;
		}
		Point pointA = (Point)startPoint;
		position = e.GetPosition(uIElement);
		if (!IsDragging(pointA, position))
		{
			return;
		}
		if (e.LeftButton == MouseButtonState.Released)
		{
			uIElement.SetValue(StartPointProperty, null);
			uIElement.SetValue(DragSourceRowProperty, null);
			return;
		}
		object data = uIElement;
		DragAdorner dragAdorner = null;
		DataGridRow dataGridRow = uIElement.GetValue(DragSourceRowProperty) as DataGridRow ?? ResolveDataGridRow(sender, e.OriginalSource);
		if (dataGridRow != null)
		{
			DataGrid dataGrid = ResolveDataGrid(sender, dataGridRow);
			if (dataGrid == null || !dataGrid.GetSelectedRows().Contains(dataGridRow))
			{
				return;
			}
			IEnumerable<DataGridRow> selectedRows = dataGrid.GetSelectedRows();
			int idx = dataGrid.Columns.TakeWhile((DataGridColumn c) => c != dataGrid.CurrentColumn).Count();
			IEnumerable<DataGridCell> enumerable = from r in selectedRows
				select dataGrid.GetCell(r, idx) into c
				where c != null
				select c;
			data = dataGrid.SelectedItems;
			if (enumerable.Count() == 0)
			{
				return;
			}
			while (dragAdorner == null)
			{
				dragAdorner = new DragAdorner(enumerable.FirstOrDefault(), enumerable, 0.5, new Point(-5.0, -5.0));
			}
		}
		if (dragAdorner == null)
		{
			dragAdorner = new DragAdorner(uIElement, 0.5, position);
		}
		SetDragAdorner(uIElement, dragAdorner);
		DragDrop.DoDragDrop(uIElement, data, DragDropEffects.Copy | DragDropEffects.Move);
		dragAdorner.Remove();
		dragAdorner = null;
		SetDragAdorner(uIElement, null);
		uIElement.SetValue(DragSourceRowProperty, null);
	}

	private static DataGridRow ResolveDataGridRow(object sender, object originalSource)
	{
		if (sender is DataGridRow row)
		{
			return row;
		}
		if (originalSource is DependencyObject dependencyObject)
		{
			return WPFUtil.FindVisualParent<DataGridRow>(dependencyObject);
		}
		return null;
	}

	private static DataGrid ResolveDataGrid(object sender, DataGridRow row)
	{
		return sender as DataGrid ?? WPFUtil.FindVisualParent<DataGrid>(row);
	}

	private static bool IsDragging(Point pointA, Point pointB)
	{
		if (Math.Abs(pointA.X - pointB.X) > SystemParameters.MinimumHorizontalDragDistance)
		{
			return true;
		}
		if (Math.Abs(pointA.Y - pointB.Y) > SystemParameters.MinimumVerticalDragDistance)
		{
			return true;
		}
		return false;
	}

	private static void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
	{
		UIElement uIElement = sender as UIElement;
		DragAdorner dragAdorner = GetDragAdorner(uIElement);
		try
		{
			if (dragAdorner != null)
			{
				dragAdorner.Position = WPFUtil.GetMousePosition(uIElement);
			}
		}
		catch (NullReferenceException)
		{
		}
	}
}
