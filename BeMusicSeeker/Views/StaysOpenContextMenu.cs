using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views;

public class StaysOpenContextMenu : ContextMenu
{
    static StaysOpenContextMenu()
    {
        fixContextMenuStaysOpen();
    }

    private static void fixContextMenuStaysOpen()
    {
        ContextMenu.IsOpenProperty.OverrideMetadata(typeof(StaysOpenContextMenu), new FrameworkPropertyMetadata(false, null, coerceIsOpen));
        ContextMenu.StaysOpenProperty.OverrideMetadata(typeof(StaysOpenContextMenu), new FrameworkPropertyMetadata(false, null, coerceStaysOpen));
    }

    private static object coerceStaysOpen(DependencyObject d, object basevalue)
    {
        d.CoerceValue(ContextMenu.IsOpenProperty);
        return basevalue;
    }

    private static object coerceIsOpen(DependencyObject d, object basevalue)
    {
        if (((ContextMenu)d).StaysOpen)
        {
            return true;
        }
        return basevalue;
    }
}
