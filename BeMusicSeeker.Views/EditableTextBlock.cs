using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace BeMusicSeeker.Views;

public partial class EditableTextBlock : UserControl, IComponentConnector, IStyleConnector
{
	private string oldText;

	public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(EditableTextBlock), new PropertyMetadata(string.Empty, OnTextChanged));

	public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register("IsEditable", typeof(bool), typeof(EditableTextBlock), new PropertyMetadata(true, OnIsEditableChanged));

	public static readonly DependencyProperty IsInEditModeProperty = DependencyProperty.Register("IsInEditMode", typeof(bool), typeof(EditableTextBlock), new PropertyMetadata(false));

	public static readonly DependencyProperty TextFormatProperty = DependencyProperty.Register("TextFormat", typeof(string), typeof(EditableTextBlock), new PropertyMetadata("{0}"));

	public static readonly RoutedEvent EditModeChangedEvent = EventManager.RegisterRoutedEvent("EditModeChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EditableTextBlock));

	public string Text
	{
		get
		{
			return (string)GetValue(TextProperty);
		}
		set
		{
			SetValue(TextProperty, value);
		}
	}

	public bool IsEditable
	{
		get
		{
			return (bool)GetValue(IsEditableProperty);
		}
		set
		{
			SetValue(IsEditableProperty, value);
		}
	}

	public bool IsInEditMode
	{
		get
		{
			if (IsEditable)
			{
				return (bool)GetValue(IsInEditModeProperty);
			}
			return false;
		}
		set
		{
			if (IsEditable)
			{
				if (value)
				{
					oldText = Text;
				}
				bool isInEditMode = IsInEditMode;
				SetValue(IsInEditModeProperty, value);
				if (isInEditMode != value)
				{
					RaiseEditModeChangedEvent();
				}
			}
		}
	}

	public string TextFormat
	{
		get
		{
			return (string)GetValue(TextFormatProperty);
		}
		set
		{
			if (value == "")
			{
				value = "{0}";
			}
			SetValue(TextFormatProperty, value);
		}
	}

	public string FormattedText => string.Format(TextFormat, Text);

	public event RoutedEventHandler EditModeChanged
	{
		add
		{
			AddHandler(EditModeChangedEvent, value);
		}
		remove
		{
			RemoveHandler(EditModeChangedEvent, value);
		}
	}

	public EditableTextBlock()
	{
		InitializeComponent();
		base.Focusable = true;
		base.FocusVisualStyle = null;
	}

	private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		(d as EditableTextBlock).Text = (string)e.NewValue;
	}

	private static void OnIsEditableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		(d as EditableTextBlock).IsEditable = (bool)e.NewValue;
	}

	private void TextBox_Loaded(object sender, RoutedEventArgs e)
	{
		TextBox obj = sender as TextBox;
		obj.Focus();
		obj.SelectAll();
	}

	private void TextBox_LostFocus(object sender, RoutedEventArgs e)
	{
		IsInEditMode = false;
	}

	private void TextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			IsInEditMode = false;
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			Text = oldText;
			IsInEditMode = false;
			e.Handled = true;
		}
	}

	public bool IsTextChanged()
	{
		return oldText != Text;
	}

	private void RaiseEditModeChangedEvent()
	{
		RoutedEventArgs e = new RoutedEventArgs(EditModeChangedEvent);
		RaiseEvent(e);
	}
}
