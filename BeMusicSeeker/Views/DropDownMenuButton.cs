using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

public sealed class DropDownMenuButton : ToggleButton
{
    public static readonly DependencyProperty DropDownContextMenuProperty = DependencyProperty.Register("DropDownContextMenu", typeof(ContextMenu), typeof(DropDownMenuButton), new UIPropertyMetadata(null));

    public ContextMenu DropDownContextMenu
    {
        get
        {
            return GetValue(DropDownContextMenuProperty) as ContextMenu;
        }
        set
        {
            SetValue(DropDownContextMenuProperty, value);
        }
    }

    public DropDownMenuButton()
    {
        var binding = new Binding("DropDownContextMenu.IsOpen")
        {
            Source = this
        };
        SetBinding(ToggleButton.IsCheckedProperty, binding);
    }

    protected override void OnClick()
    {
        if (DropDownContextMenu != null)
        {
            DropDownContextMenu.PlacementTarget = this;
            DropDownContextMenu.Placement = PlacementMode.Bottom;
            NLogWrapper.DebuggerLogger?.Trace(base.IsChecked + " " + DropDownContextMenu.IsOpen.ToString());
            DropDownContextMenu.IsOpen = true;
            NLogWrapper.DebuggerLogger?.Trace(base.IsChecked + " " + DropDownContextMenu.StaysOpen.ToString());
        }
    }
}
