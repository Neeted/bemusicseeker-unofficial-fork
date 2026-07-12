using System;
using System.Windows.Markup;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

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

    private readonly Func<bool> firstStartupProvider;

    private readonly Action completeFirstStartup;

    private readonly Action reloadSettings;

    private readonly Action saveSettings;

    private readonly IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    internal ApplicationComposition(
        Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider,
        Func<StartupSettingsSnapshot> startupSettingsProvider = null,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider = null,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        IMainChartColumnSettingsStore mainChartColumnSettingsStore = null,
        Func<bool> firstStartupProvider = null,
        Action completeFirstStartup = null,
        Action reloadSettings = null,
        Action saveSettings = null,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore = null,
        IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore = null)
    {
        this.bmsLibraryOptionsProvider = bmsLibraryOptionsProvider ?? throw new ArgumentNullException(nameof(bmsLibraryOptionsProvider));
        this.startupSettingsProvider = startupSettingsProvider ?? StartupSettingsSnapshot.CreateCurrent;
        this.playlistUrlCompletionOptionsProvider = playlistUrlCompletionOptionsProvider ?? PlaylistUrlCompletionOptionsSnapshot.CreateCurrent;
        this.beatorajaBmtOptionsProvider = beatorajaBmtOptionsProvider ?? BeatorajaBmtOptionsSnapshot.CreateCurrent;
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider ?? CustomFolderOutputSettingsSnapshot.CreateCurrent;
        this.mainChartColumnSettingsStore = mainChartColumnSettingsStore
            ?? new SettingsMainChartColumnSettingsStore();
        this.firstStartupProvider = firstStartupProvider
            ?? (() => GetApplication().firstStartup);
        this.completeFirstStartup = completeFirstStartup
            ?? (() => GetApplication().firstStartup = false);
        this.reloadSettings = reloadSettings
            ?? (() => Settings.Default.Reload());
        this.saveSettings = saveSettings
            ?? (() => Settings.Default.Save());
        this.keywordSearchHistorySettingsStore = keywordSearchHistorySettingsStore
            ?? new SettingsKeywordSearchHistorySettingsStore();
        this.playHistoryDisplaySettingsStore = playHistoryDisplaySettingsStore
            ?? new SettingsPlayHistoryDisplaySettingsStore();
    }

    internal Func<BmsLibraryOptionsSnapshot> BmsLibraryOptionsProvider => bmsLibraryOptionsProvider;

    internal Func<StartupSettingsSnapshot> StartupSettingsProvider => startupSettingsProvider;

    internal Func<PlaylistUrlCompletionOptionsSnapshot> PlaylistUrlCompletionOptionsProvider => playlistUrlCompletionOptionsProvider;

    internal Func<BeatorajaBmtOptionsSnapshot> BeatorajaBmtOptionsProvider => beatorajaBmtOptionsProvider;

    internal Func<CustomFolderOutputSettingsSnapshot> CustomFolderOutputSettingsProvider => customFolderOutputSettingsProvider;

    internal IMainChartColumnSettingsStore MainChartColumnSettingsStore => mainChartColumnSettingsStore;

    internal Func<bool> FirstStartupProvider => firstStartupProvider;

    internal Action CompleteFirstStartup => completeFirstStartup;

    internal Action ReloadSettings => reloadSettings;

    internal Action SaveSettings => saveSettings;

    internal IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore => keywordSearchHistorySettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    private static App GetApplication()
    {
        return (App)System.Windows.Application.Current;
    }

    internal MainChartListViewModel CreateMainChartListViewModel(
        Action<Action> dispatchPresentationAction,
        Action<string> log)
    {
        return new MainChartListViewModel(
            dispatchPresentationAction,
            log,
            mainChartColumnSettingsStore);
    }

    internal PlaylistWorkspaceViewModel CreatePlaylistWorkspaceViewModel(
        Action<Action> dispatchPresentationAction,
        MainChartListViewModel mainChartList,
        PlaylistDetailBuildState playlistDetailBuildState,
        PlaylistDetailViewState playlistViewState,
        Action<string> detailViewLog,
        Action<string> detailRetentionLog)
    {
        return new PlaylistWorkspaceViewModel(
            dispatchPresentationAction,
            mainChartList,
            playlistDetailBuildState,
            playlistViewState,
            detailViewLog,
            detailRetentionLog);
    }

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
