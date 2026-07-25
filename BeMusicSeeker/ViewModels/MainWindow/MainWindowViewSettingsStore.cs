using System;
using System.ComponentModel;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Provides the settings owned by the MainWindow view-host boundary.
/// </summary>
public interface IMainWindowViewSettingsStore : INotifyPropertyChanged
{
    double TreeViewWidth { get; }

    bool StartupSelectInstallPending { get; }

    double CustomTableRowHeight { get; }

    double CustomTableHeaderHeight { get; }

    double CustomTableFontSize { get; }

    WindowPlacement WindowPlacement { get; }

    void CaptureTreeViewWidth(double actualColumnWidth, double assignedColumnWidth);

    void CaptureWindowPlacement(WindowPlacement windowPlacement);
}

/// <summary>
/// Adapts the persisted settings to the MainWindow view-host boundary.
/// </summary>
internal sealed class SettingsMainWindowViewSettingsStore : IMainWindowViewSettingsStore
{
    private readonly Func<Settings> settingsProvider;

    internal SettingsMainWindowViewSettingsStore(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        Values.PropertyChanged += ValuesPropertyChanged;
    }

    private Settings Values => settingsProvider()
        ?? throw new InvalidOperationException("MainWindow view settings provider returned null.");

    public event PropertyChangedEventHandler PropertyChanged;

    public double TreeViewWidth
    {
        get
        {
            Settings values = Values;
            double persistedWidth = (double)values["TreeViewWidth"];
            double normalizedWidth = Settings.NormalizeTreeViewWidth(persistedWidth);
            if (!normalizedWidth.Equals(persistedWidth))
            {
                values.TreeViewWidth = normalizedWidth;
            }
            return normalizedWidth;
        }
    }

    public bool StartupSelectInstallPending => Values.StartupSelectInstallPending;

    public double CustomTableRowHeight => Values.CustomTableRowHeight;

    public double CustomTableHeaderHeight => Values.CustomTableHeaderHeight;

    public double CustomTableFontSize => Values.CustomTableFontSize;

    public WindowPlacement WindowPlacement => Win32WindowPlacementAdapter.FromNative(Values.WindowPlacement);

    public void CaptureTreeViewWidth(double actualColumnWidth, double assignedColumnWidth)
    {
        Settings values = Values;
        values.TreeViewWidth = MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(
            actualColumnWidth,
            assignedColumnWidth,
            values.TreeViewWidth);
    }

    public void CaptureWindowPlacement(WindowPlacement windowPlacement)
    {
        Values.WindowPlacement = Win32WindowPlacementAdapter.ToNative(windowPlacement);
    }

    private void ValuesPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e?.PropertyName))
        {
            RaisePropertyChanged(nameof(TreeViewWidth));
            RaisePropertyChanged(nameof(StartupSelectInstallPending));
            RaisePropertyChanged(nameof(CustomTableRowHeight));
            RaisePropertyChanged(nameof(CustomTableHeaderHeight));
            RaisePropertyChanged(nameof(CustomTableFontSize));
            RaisePropertyChanged(nameof(WindowPlacement));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(Settings.TreeViewWidth):
                RaisePropertyChanged(nameof(TreeViewWidth));
                break;
            case nameof(Settings.StartupSelectInstallPending):
                RaisePropertyChanged(nameof(StartupSelectInstallPending));
                break;
            case nameof(Settings.CustomTableRowHeight):
                RaisePropertyChanged(nameof(CustomTableRowHeight));
                break;
            case nameof(Settings.CustomTableHeaderHeight):
                RaisePropertyChanged(nameof(CustomTableHeaderHeight));
                break;
            case nameof(Settings.CustomTableFontSize):
                RaisePropertyChanged(nameof(CustomTableFontSize));
                break;
            case nameof(Settings.WindowPlacement):
                RaisePropertyChanged(nameof(WindowPlacement));
                break;
        }
    }

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static class MainWindowViewSettingsPolicy
{
    internal static double ResolveTreeViewWidthForSave(
        double actualColumnWidth,
        double assignedColumnWidth,
        double currentSettingWidth)
    {
        if (IsUsableTreeViewWidth(actualColumnWidth))
        {
            return actualColumnWidth;
        }
        if (IsUsableTreeViewWidth(assignedColumnWidth))
        {
            return assignedColumnWidth;
        }
        return Settings.NormalizeTreeViewWidth(currentSettingWidth);
    }

    private static bool IsUsableTreeViewWidth(double width)
    {
        return !double.IsNaN(width)
            && !double.IsInfinity(width)
            && width >= Settings.MinTreeViewWidth;
    }
}
