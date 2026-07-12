using System;
using System.Windows.Markup;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// アプリケーション起動時に ViewModel へ渡す production composition を構築します。
/// </summary>
internal sealed class ApplicationComposition
{
    private readonly Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider;

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    private readonly Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider;

    private readonly Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly IMainChartColumnSettingsStore mainChartColumnSettingsStore;

    internal ApplicationComposition(
        Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider,
        Func<StartupSettingsSnapshot> startupSettingsProvider = null,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider = null,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        IMainChartColumnSettingsStore mainChartColumnSettingsStore = null)
    {
        this.bmsLibraryOptionsProvider = bmsLibraryOptionsProvider ?? throw new ArgumentNullException(nameof(bmsLibraryOptionsProvider));
        this.startupSettingsProvider = startupSettingsProvider ?? StartupSettingsSnapshot.CreateCurrent;
        this.playlistUrlCompletionOptionsProvider = playlistUrlCompletionOptionsProvider ?? PlaylistUrlCompletionOptionsSnapshot.CreateCurrent;
        this.beatorajaBmtOptionsProvider = beatorajaBmtOptionsProvider ?? BeatorajaBmtOptionsSnapshot.CreateCurrent;
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider ?? CustomFolderOutputSettingsSnapshot.CreateCurrent;
        this.mainChartColumnSettingsStore = mainChartColumnSettingsStore
            ?? new SettingsMainChartColumnSettingsStore();
    }

    internal Func<BmsLibraryOptionsSnapshot> BmsLibraryOptionsProvider => bmsLibraryOptionsProvider;

    internal Func<StartupSettingsSnapshot> StartupSettingsProvider => startupSettingsProvider;

    internal Func<PlaylistUrlCompletionOptionsSnapshot> PlaylistUrlCompletionOptionsProvider => playlistUrlCompletionOptionsProvider;

    internal Func<BeatorajaBmtOptionsSnapshot> BeatorajaBmtOptionsProvider => beatorajaBmtOptionsProvider;

    internal Func<CustomFolderOutputSettingsSnapshot> CustomFolderOutputSettingsProvider => customFolderOutputSettingsProvider;

    internal IMainChartColumnSettingsStore MainChartColumnSettingsStore => mainChartColumnSettingsStore;

    internal static ApplicationComposition CreateDefault()
    {
        return new ApplicationComposition(
            BmsLibraryOptionsSnapshot.CreateCurrent,
            StartupSettingsSnapshot.CreateCurrent,
            PlaylistUrlCompletionOptionsSnapshot.CreateCurrent,
            BeatorajaBmtOptionsSnapshot.CreateCurrent,
            CustomFolderOutputSettingsSnapshot.CreateCurrent,
            new SettingsMainChartColumnSettingsStore());
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
