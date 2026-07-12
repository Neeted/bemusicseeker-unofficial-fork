using System;
using System.Windows.Markup;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// アプリケーション起動時に ViewModel へ渡す production composition を構築します。
/// </summary>
internal sealed class ApplicationComposition
{
    private readonly Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider;

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    internal ApplicationComposition(
        Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider,
        Func<StartupSettingsSnapshot> startupSettingsProvider = null)
    {
        this.bmsLibraryOptionsProvider = bmsLibraryOptionsProvider ?? throw new ArgumentNullException(nameof(bmsLibraryOptionsProvider));
        this.startupSettingsProvider = startupSettingsProvider ?? StartupSettingsSnapshot.CreateCurrent;
    }

    internal Func<BmsLibraryOptionsSnapshot> BmsLibraryOptionsProvider => bmsLibraryOptionsProvider;

    internal Func<StartupSettingsSnapshot> StartupSettingsProvider => startupSettingsProvider;

    internal static ApplicationComposition CreateDefault()
    {
        return new ApplicationComposition(
            BmsLibraryOptionsSnapshot.CreateCurrent,
            StartupSettingsSnapshot.CreateCurrent);
    }

    internal MainWindowViewModel CreateMainWindowViewModel()
    {
        return new MainWindowViewModel(this);
    }
}

/// <summary>
/// XAML resource から MainWindowViewModel を application composition 経由で生成します。
/// </summary>
public sealed class MainWindowViewModelResourceExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return ApplicationComposition.CreateDefault().CreateMainWindowViewModel();
    }
}
