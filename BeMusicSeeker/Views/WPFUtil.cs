using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;

namespace BeMusicSeeker.Views;

public static class WPFUtil
{
	private struct POINT
	{
		public uint X;

		public uint Y;
	}

	public static T FindVisualParent<T>(DependencyObject d) where T : DependencyObject
	{
		if (d == null)
		{
			return null;
		}
		try
		{
			DependencyObject parent = VisualTreeHelper.GetParent(d);
			if (parent == null || !(parent is T result))
			{
				T val = FindVisualParent<T>(parent);
				if (val != null)
				{
					return val;
				}
				return null;
			}
			return result;
		}
		catch
		{
			if (d is FrameworkElement)
			{
				FrameworkElement frameworkElement = (FrameworkElement)d;
				if (frameworkElement.Parent is T)
				{
					return frameworkElement.Parent as T;
				}
				return FindVisualParent<T>(frameworkElement.Parent);
			}
			if (d is FrameworkContentElement)
			{
				FrameworkContentElement frameworkContentElement = (FrameworkContentElement)d;
				if (frameworkContentElement.Parent is T)
				{
					return frameworkContentElement.Parent as T;
				}
				return FindVisualParent<T>(frameworkContentElement.Parent);
			}
			return null;
		}
	}

	public static T FindVisualParent<T>(DependencyObject d, string name) where T : DependencyObject
	{
		DependencyObject d2 = d;
		while (true)
		{
			DependencyObject dependencyObject = FindVisualParent<T>(d2);
			if (dependencyObject == null)
			{
				return null;
			}
			if (dependencyObject is FrameworkElement)
			{
				if (((FrameworkElement)dependencyObject).Name == name)
				{
					return dependencyObject as T;
				}
			}
			else
			{
				if (!(dependencyObject is FrameworkContentElement))
				{
					break;
				}
				if (((FrameworkContentElement)dependencyObject).Name == name)
				{
					return dependencyObject as T;
				}
			}
			d2 = dependencyObject;
		}
		return null;
	}

	public static T FindVisualChild<T>(DependencyObject d) where T : DependencyObject
	{
		if (d == null)
		{
			return null;
		}
		try
		{
			for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(d) - 1; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(d, i);
				if (child == null || !(child is T result))
				{
					T val = FindVisualChild<T>(child);
					if (val != null)
					{
						return val;
					}
					continue;
				}
				return result;
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	public static T FindVisualChild<T>(DependencyObject d, string name) where T : DependencyObject
	{
		if (d == null)
		{
			return null;
		}
		try
		{
			for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(d) - 1; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(d, i);
				if (child != null && child is T)
				{
					if (child is FrameworkElement)
					{
						if (((FrameworkElement)child).Name == name)
						{
							return child as T;
						}
					}
					else
					{
						if (!(child is FrameworkContentElement))
						{
							return null;
						}
						if (((FrameworkContentElement)child).Name == name)
						{
							return child as T;
						}
					}
				}
				T val = FindVisualChild<T>(child, name);
				if (val != null)
				{
					return val;
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	public static T FindVisualChildSearchedByDataContext<T>(DependencyObject d, object x) where T : DependencyObject
	{
		if (d == null)
		{
			return null;
		}
		try
		{
			for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(d) - 1; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(d, i);
				if (child != null)
				{
					if (child is FrameworkElement)
					{
						if (((FrameworkElement)child).DataContext == x)
						{
							return child as T;
						}
					}
					else if (child is FrameworkContentElement)
					{
						if (((FrameworkContentElement)child).DataContext == x)
						{
							return child as T;
						}
					}
				}
				T val = FindVisualChildSearchedByDataContext<T>(child, x);
				if (val != null)
				{
					return val;
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	public static bool FindVisualChildPath(DependencyObject d, DependencyObject x, ref List<object> result)
	{
		if (d == null)
		{
			return false;
		}
		try
		{
			for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(d) - 1; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(d, i);
				if (child != null && child != null)
				{
					if (child is FrameworkElement)
					{
						if ((FrameworkElement)child == x)
						{
							result.Add(child);
							return true;
						}
					}
					else
					{
						if (!(child is FrameworkContentElement))
						{
							return false;
						}
						if ((FrameworkContentElement)child == x)
						{
							result.Add(child);
							return true;
						}
					}
				}
				if (FindVisualChildPath(child, x, ref result))
				{
					result.Add(child);
					return true;
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public static bool FindVisualChildPathSearchedByDataContext(DependencyObject d, object x, ref List<object> result)
	{
		if (d == null)
		{
			return false;
		}
		try
		{
			for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(d) - 1; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(d, i);
				if (child != null)
				{
					if (child is FrameworkElement)
					{
						if (((FrameworkElement)child).DataContext == x)
						{
							result.Add(child);
							return true;
						}
					}
					else
					{
						if (!(child is FrameworkContentElement))
						{
							return false;
						}
						if (((FrameworkContentElement)child).DataContext == x)
						{
							result.Add(child);
							return true;
						}
					}
				}
				if (FindVisualChildPathSearchedByDataContext(child, x, ref result))
				{
					result.Add(child);
					return true;
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	[DllImport("user32.dll")]
	private static extern void GetCursorPos(out POINT pt);

	[DllImport("user32.dll")]
	private static extern int ScreenToClient(IntPtr hwnd, ref POINT pt);

	public static Point GetMousePosition(Visual visual)
	{
		GetCursorPos(out var pt);
		ScreenToClient(((HwndSource)PresentationSource.FromVisual(visual)).Handle, ref pt);
		return new Point(pt.X, pt.Y);
	}

	public static T GetVisualChild<T>(Visual parent) where T : Visual
	{
		T val = null;
		int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
		for (int i = 0; i < childrenCount; i++)
		{
			Visual visual = (Visual)VisualTreeHelper.GetChild(parent, i);
			val = visual as T;
			if (val == null)
			{
				val = GetVisualChild<T>(visual);
			}
			if (val != null)
			{
				break;
			}
		}
		return val;
	}

	public static List<T> GetVisualChildren<T>(Visual parent) where T : Visual
	{
		T val = null;
		int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
		List<T> list = new List<T>();
		List<Visual> list2 = new List<Visual>();
		for (int i = 0; i < childrenCount; i++)
		{
			Visual visual = (Visual)VisualTreeHelper.GetChild(parent, i);
			val = visual as T;
			if (val == null)
			{
				list2.Add(visual);
			}
			if (val != null)
			{
				list.Add(val);
			}
		}
		if (list.Count > 0)
		{
			return list;
		}
		if (list2.Count > 0)
		{
			return list2.SelectMany((Visual parent2) => GetVisualChildren<T>(parent2)).ToList();
		}
		return list;
	}

	public static UIElement DeepCopy(UIElement element)
	{
		return (UIElement)XamlReader.Load(new XmlTextReader(new StringReader(XamlWriter.Save(element))));
	}
}
