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
    private const int ShowCommandNormal = 1;

    private const int ShowCommandMinimized = 2;

    private const int ShowCommandMaximized = 3;

    /// <summary>最小化の復元先が最大化であることを示す GetWindowPlacement のフラグ（WPF_RESTORETOMAXIMIZED）です。</summary>
    private const int RestoreToMaximizedFlag = 0x0002;

    /// <summary>
    /// 保存した配置を、次回起動で SetWindowPlacement へ渡す配置へ変換します。
    /// 最小化のまま終了した場合に起動直後を最小化にしないため、最小化は「元に戻すと最大化」のフラグがあれば最大化、なければ通常にします。
    /// 最大化・通常・その他の値は従来どおり渡し、位置と大きさは変えません。渡す Flags は常に0です。
    /// </summary>
    /// <param name="saved">保存した配置。</param>
    /// <returns>復元に使う配置。</returns>
    internal static WindowPlacement ResolveRestorePlacement(WindowPlacement saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        int showCommand = saved.ShowCommand == ShowCommandMinimized
            ? ((saved.Flags & RestoreToMaximizedFlag) != 0 ? ShowCommandMaximized : ShowCommandNormal)
            : saved.ShowCommand;
        return new WindowPlacement(
            flags: 0,
            showCommand,
            saved.MinX,
            saved.MinY,
            saved.MaxX,
            saved.MaxY,
            saved.Left,
            saved.Top,
            saved.Right,
            saved.Bottom);
    }

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
