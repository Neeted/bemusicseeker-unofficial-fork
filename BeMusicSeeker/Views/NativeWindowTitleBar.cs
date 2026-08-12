using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

/// <summary>
/// Describes the colors and immersive mode requested for a native application window caption.
/// </summary>
/// <param name="UseDarkMode">Whether Windows should render dark-mode caption controls.</param>
/// <param name="CaptionColor">The caption background color.</param>
/// <param name="TextColor">The caption text color.</param>
/// <param name="BorderColor">The native window border color.</param>
internal readonly record struct NativeWindowTitleBarAppearance(
    bool UseDarkMode,
    Color CaptionColor,
    Color TextColor,
    Color BorderColor);

/// <summary>
/// Applies the application appearance to a native top-level window handle.
/// </summary>
internal interface INativeWindowTitleBarGateway
{
    /// <summary>
    /// Applies supported native caption attributes without changing the standard Window chrome.
    /// </summary>
    /// <param name="windowHandle">The initialized native window handle.</param>
    /// <param name="appearance">The appearance derived from the current application theme.</param>
    void Apply(IntPtr windowHandle, NativeWindowTitleBarAppearance appearance);
}

/// <summary>
/// Supplies the currently active theme colors and change notifications to one native window.
/// </summary>
internal interface INativeWindowTitleBarThemeSource
{
    /// <summary>Occurs after the application theme resource dictionary changes.</summary>
    event EventHandler ThemeChanged;

    /// <summary>
    /// Reads all native caption inputs from the active semantic palette.
    /// </summary>
    /// <param name="appearance">The complete appearance when all required brushes are available.</param>
    /// <returns>The complete native caption appearance.</returns>
    NativeWindowTitleBarAppearance GetAppearance();
}

/// <summary>
/// Owns title-bar application and theme subscription for exactly one native window lifetime.
/// </summary>
internal sealed class NativeWindowTitleBarController : IDisposable
{
    private readonly INativeWindowTitleBarGateway gateway;
    private readonly INativeWindowTitleBarThemeSource themeSource;
    private IntPtr windowHandle;
    private bool attached;

    /// <summary>
    /// Creates a lifetime controller around explicit native and theme boundaries.
    /// </summary>
    /// <param name="gateway">The native title-bar gateway.</param>
    /// <param name="themeSource">The current Settings title-bar theme source.</param>
    internal NativeWindowTitleBarController(
        INativeWindowTitleBarGateway gateway,
        INativeWindowTitleBarThemeSource themeSource)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
    }

    /// <summary>
    /// Attaches after source initialization, applies the current theme, and observes later theme changes.
    /// </summary>
    /// <param name="handle">The initialized window handle.</param>
    internal void Attach(IntPtr handle)
    {
        if (attached || handle == IntPtr.Zero)
        {
            return;
        }

        themeSource.ThemeChanged += ThemeSourceThemeChanged;
        attached = true;
        windowHandle = handle;
        try
        {
            ApplyCurrentTheme();
        }
        catch
        {
            attached = false;
            windowHandle = IntPtr.Zero;
            themeSource.ThemeChanged -= ThemeSourceThemeChanged;
            throw;
        }
    }

    /// <summary>
    /// Ends the theme subscription. Native attributes remain owned by Windows after the handle closes.
    /// </summary>
    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        attached = false;
        windowHandle = IntPtr.Zero;
        themeSource.ThemeChanged -= ThemeSourceThemeChanged;
    }

    private void ThemeSourceThemeChanged(object sender, EventArgs e)
    {
        ApplyCurrentTheme();
    }

    private void ApplyCurrentTheme()
    {
        if (attached)
        {
            gateway.Apply(windowHandle, themeSource.GetAppearance());
        }
    }
}

/// <summary>
/// Resolves native title-bar appearance from the application theme service and semantic resources.
/// </summary>
internal sealed class AppNativeWindowTitleBarThemeSource : INativeWindowTitleBarThemeSource
{
    /// <inheritdoc />
    public event EventHandler ThemeChanged
    {
        add => AppThemeService.ThemeChanged += value;
        remove => AppThemeService.ThemeChanged -= value;
    }

    /// <inheritdoc />
    public NativeWindowTitleBarAppearance GetAppearance()
    {
        return new NativeWindowTitleBarAppearance(
            AppThemeService.IsDarkTheme(global::BeMusicSeeker.Properties.Settings.Default.AppearanceTheme),
            GetRequiredSolidColorBrush("App.DialogBackgroundBrush").Color,
            GetRequiredSolidColorBrush("App.TextBrush").Color,
            GetRequiredSolidColorBrush("App.BorderBrush").Color);
    }

    private static SolidColorBrush GetRequiredSolidColorBrush(string resourceKey)
    {
        object resource = Application.Current?.TryFindResource(resourceKey)
            ?? throw new InvalidOperationException($"Required theme resource '{resourceKey}' is unavailable.");
        return resource as SolidColorBrush
            ?? throw new InvalidOperationException($"Required theme resource '{resourceKey}' must be a SolidColorBrush.");
    }
}

/// <summary>
/// Uses supported Desktop Window Manager attributes to theme a standard native title bar.
/// </summary>
internal sealed partial class DwmNativeWindowTitleBarGateway : INativeWindowTitleBarGateway
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    /// <inheritdoc />
    public void Apply(IntPtr windowHandle, NativeWindowTitleBarAppearance appearance)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        int darkMode = appearance.UseDarkMode ? 1 : 0;
        if (!TrySetAttribute(windowHandle, UseImmersiveDarkMode, darkMode))
        {
            TrySetAttribute(windowHandle, UseImmersiveDarkModeBefore20H1, darkMode);
        }

        TrySetAttribute(windowHandle, CaptionColor, ToColorRef(appearance.CaptionColor));
        TrySetAttribute(windowHandle, TextColor, ToColorRef(appearance.TextColor));
        TrySetAttribute(windowHandle, BorderColor, ToColorRef(appearance.BorderColor));
    }

    private static bool TrySetAttribute(IntPtr windowHandle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(windowHandle, attribute, ref value, sizeof(int)) >= 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
