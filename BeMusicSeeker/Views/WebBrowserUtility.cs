using System;
using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views;

public static class WebBrowserUtility
{
    public static readonly DependencyProperty BindableSourceProperty = DependencyProperty.RegisterAttached("BindableSource", typeof(object), typeof(WebBrowserUtility), new UIPropertyMetadata(null, BindableSourcePropertyChanged));

    public static readonly DependencyProperty HtmlProperty = DependencyProperty.RegisterAttached("Html", typeof(string), typeof(WebBrowserUtility), new FrameworkPropertyMetadata(OnHtmlChanged));

    public static object GetBindableSource(DependencyObject obj)
    {
        return (string)obj.GetValue(BindableSourceProperty);
    }

    public static void SetBindableSource(DependencyObject obj, object value)
    {
        obj.SetValue(BindableSourceProperty, value);
    }

    public static void BindableSourcePropertyChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is WebBrowser webBrowser)
        {
            Uri source = null;
            if (e.NewValue is string)
            {
                string text = e.NewValue as string;
                source = (string.IsNullOrWhiteSpace(text) ? null : new Uri(text));
            }
            else if (e.NewValue is Uri)
            {
                source = e.NewValue as Uri;
            }
            webBrowser.Source = source;
        }
    }

    [AttachedPropertyBrowsableForType(typeof(WebBrowser))]
    public static string GetHtml(WebBrowser d)
    {
        return (string)d.GetValue(HtmlProperty);
    }

    public static void SetHtml(WebBrowser d, string value)
    {
        d.SetValue(HtmlProperty, value);
    }

    private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WebBrowser webBrowser)
        {
            webBrowser.NavigateToString(e.NewValue as string);
        }
    }
}
