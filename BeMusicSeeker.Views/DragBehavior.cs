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
		bool handled = true;
		if (sender is DataGridRow)
		{
			DataGrid dataGrid = WPFUtil.FindVisualParent<DataGrid>(sender as DataGridRow);
			if (dataGrid != null)
			{
				if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
				{
					handled = false;
					dataGrid.SelectionMode = DataGridSelectionMode.Extended;
				}
				else if (dataGrid.SelectedItems.Count <= 1 || !dataGrid.GetSelectedRows().Contains(sender))
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
		}
		NLogWrapper.DebuggerLogger?.Trace(handled.ToString());
		e.Handled = handled;
		UIElement uIElement = sender as UIElement;
		uIElement.SetValue(StartPointProperty, e.GetPosition(uIElement));
	}

	private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		UIElement obj = sender as UIElement;
		if (sender is DataGridRow)
		{
			DataGrid dataGrid = WPFUtil.FindVisualParent<DataGrid>(sender as DataGridRow);
			if (dataGrid != null && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift) && !Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl) && dataGrid.SelectedItems.Count > 1 && dataGrid.GetSelectedRows().Contains(sender) && e.ChangedButton != MouseButton.Right)
			{
				dataGrid.UnselectAllCells();
				dataGrid.UnselectAll();
				dataGrid.SelectedItem = ((DataGridRow)sender).DataContext;
			}
		}
		obj.SetValue(StartPointProperty, null);
	}

	private static void OnMouseMove(object sender, MouseEventArgs e)
	{
		UIElement uIElement = sender as UIElement;
		Point position = e.GetPosition(uIElement);
		if (uIElement.GetValue(StartPointProperty) != null)
		{
			Point pointA = (Point)uIElement.GetValue(StartPointProperty);
			position = e.GetPosition(uIElement);
			if (!IsDragging(pointA, position))
			{
				return;
			}
		}
		if (e.LeftButton == MouseButtonState.Released)
		{
			uIElement.SetValue(StartPointProperty, null);
			return;
		}
		object data = uIElement;
		DragAdorner dragAdorner = null;
		if (uIElement is DataGridRow)
		{
			DataGrid dataGrid = WPFUtil.FindVisualParent<DataGrid>(uIElement);
			if (dataGrid == null || !dataGrid.GetSelectedRows().Contains(uIElement))
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
