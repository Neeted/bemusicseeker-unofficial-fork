using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Parago.Windows.Controls;

public class WindowSettings
{
    public static readonly DependencyProperty HideCloseButtonProperty = DependencyProperty.RegisterAttached("HideCloseButton", typeof(bool), typeof(WindowSettings), new FrameworkPropertyMetadata(false, OnHideCloseButtonPropertyChanged));

    private static readonly RoutedEventHandler OnWindowLoaded = delegate (object s, RoutedEventArgs e)
    {
        if (s is Window)
        {
            var obj = s as Window;
            HideCloseButton(obj);
            obj.Loaded -= OnWindowLoaded;
        }
    };

    private static readonly DependencyPropertyKey IsHiddenCloseButtonKey = DependencyProperty.RegisterAttachedReadOnly("IsCloseButtonHidden", typeof(bool), typeof(WindowSettings), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsCloseButtonHiddenProperty = IsHiddenCloseButtonKey.DependencyProperty;

    private const int GWL_STYLE = -16;

    private const int WS_SYSMENU = 524288;

    public static bool GetHideCloseButton(FrameworkElement element)
    {
        return (bool)element.GetValue(HideCloseButtonProperty);
    }

    public static void SetHideCloseButton(FrameworkElement element, bool hideCloseButton)
    {
        element.SetValue(HideCloseButtonProperty, hideCloseButton);
    }

    private static void OnHideCloseButtonPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
        {
            return;
        }
        bool flag = (bool)e.NewValue;
        if (flag && !GetIsCloseButtonHidden(window))
        {
            if (!window.IsLoaded)
            {
                window.Loaded += OnWindowLoaded;
            }
            else
            {
                HideCloseButton(window);
            }
            SetIsCloseButtonHidden(window, isCloseButtonHidden: true);
        }
        else if (!flag && GetIsCloseButtonHidden(window))
        {
            if (!window.IsLoaded)
            {
                window.Loaded -= OnWindowLoaded;
            }
            else
            {
                ShowCloseButton(window);
            }
            SetIsCloseButtonHidden(window, isCloseButtonHidden: false);
        }
    }

    public static bool GetIsCloseButtonHidden(FrameworkElement element)
    {
        return (bool)element.GetValue(IsCloseButtonHiddenProperty);
    }

    private static void SetIsCloseButtonHidden(FrameworkElement element, bool isCloseButtonHidden)
    {
        element.SetValue(IsHiddenCloseButtonKey, isCloseButtonHidden);
    }

    private static void HideCloseButton(Window w)
    {
        IntPtr handle = new WindowInteropHelper(w).Handle;
        SetWindowLong(handle, -16, GetWindowLong(handle, -16) & -524289);
    }

    private static void ShowCloseButton(Window w)
    {
        IntPtr handle = new WindowInteropHelper(w).Handle;
        SetWindowLong(handle, -16, GetWindowLong(handle, -16) | 0x80000);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
