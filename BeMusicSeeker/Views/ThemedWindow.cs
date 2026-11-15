using System;
using System.Windows;
using System.Windows.Interop;

namespace BeMusicSeeker.Views;

/// <summary>
/// Provides application surface resources and native title-bar theming for one top-level window lifetime.
/// </summary>
public class ThemedWindow : Window
{
    private readonly NativeWindowTitleBarController titleBarController;

    /// <summary>Initializes a window with the application theme and supported native title-bar attributes.</summary>
    public ThemedWindow()
        : this(new DwmNativeWindowTitleBarGateway(), new AppNativeWindowTitleBarThemeSource())
    {
    }

    /// <summary>Initializes a window with explicit native title-bar boundaries.</summary>
    /// <param name="titleBarGateway">The boundary that applies native title-bar attributes.</param>
    /// <param name="titleBarThemeSource">The source of semantic title-bar colors and theme changes.</param>
    private protected ThemedWindow(
        INativeWindowTitleBarGateway titleBarGateway,
        INativeWindowTitleBarThemeSource titleBarThemeSource)
    {
        titleBarController = new NativeWindowTitleBarController(titleBarGateway, titleBarThemeSource);
        SetResourceReference(BackgroundProperty, "App.DialogBackgroundBrush");
        SetResourceReference(ForegroundProperty, "App.TextBrush");
        SetResourceReference(FontFamilyProperty, BeMusicSeeker.Models.AppFontService.ResourceKey);
        // 小さい文字を画素に合わせて描き、内部の部品へ継承させる。
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        titleBarController.Attach(new WindowInteropHelper(this).Handle);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        titleBarController.Dispose();
        base.OnClosed(e);
    }
}
