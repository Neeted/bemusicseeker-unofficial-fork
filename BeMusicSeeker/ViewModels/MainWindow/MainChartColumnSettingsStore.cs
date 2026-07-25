using System;
using System.Collections.Generic;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal interface IMainChartColumnSettingsStore
{
    CustomTableColumnSettings GetMain(CustomTableColumnSettings.ViewKind viewKind, bool reset);

    bool IsMainReady(CustomTableColumnSettings.ViewKind viewKind);

    PlaylistSummaryColumnSettings GetPlaylistSummary(bool ensureCompatibility);

    PlaylistSummaryColumnSettings ResetPlaylistSummary();
}

internal sealed class SettingsMainChartColumnSettingsStore : IMainChartColumnSettingsStore
{
    private readonly Func<Settings> settingsProvider;

    internal SettingsMainChartColumnSettingsStore(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider
            ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    public CustomTableColumnSettings GetMain(CustomTableColumnSettings.ViewKind viewKind, bool reset)
    {
        Settings settings = settingsProvider();
        CustomTableColumnSettings columns = GetMain(settings, viewKind);
        if (reset || columns == null)
        {
            columns = new CustomTableColumnSettings(viewKind);
            SetMain(settings, viewKind, columns);
        }
        if (viewKind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY)
        {
            columns.EnsurePlayHistoryColumnDefaults();
        }
        return columns;
    }

    public bool IsMainReady(CustomTableColumnSettings.ViewKind viewKind)
    {
        return GetMain(settingsProvider(), viewKind) != null;
    }

    public PlaylistSummaryColumnSettings GetPlaylistSummary(bool ensureCompatibility)
    {
        Settings settings = settingsProvider();
        if (ensureCompatibility)
        {
            settings.PlaylistSummaryColumnsSettings ??= new PlaylistSummaryColumnSettings();
            settings.PlaylistSummaryColumnsSettings.EnsureCompatibility();
        }
        return settings.PlaylistSummaryColumnsSettings;
    }

    public PlaylistSummaryColumnSettings ResetPlaylistSummary()
    {
        Settings settings = settingsProvider();
        settings.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        settings.PlaylistSummaryColumnsSettings.EnsureCompatibility();
        return settings.PlaylistSummaryColumnsSettings;
    }

    private static CustomTableColumnSettings GetMain(
        Settings settings,
        CustomTableColumnSettings.ViewKind viewKind)
    {
        return viewKind switch
        {
            CustomTableColumnSettings.ViewKind.STANDARD => settings.StandardCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.UNREGISTERED => settings.UnregisteredCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.ZERO_NOTE => settings.ZeroNoteCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.PLAYLIST => settings.PlaylistCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.FULLSCAN => settings.FullScanCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.DUPLICATE => settings.DuplicateCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.ENCODING => settings.EncodingCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.INSTALL => settings.InstallCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR => settings.ChartInfoParseErrorCustomTableColumnSettings,
            CustomTableColumnSettings.ViewKind.PLAY_HISTORY => settings.PlayHistoryCustomTableColumnSettings,
            _ => null
        };
    }

    private static void SetMain(
        Settings settings,
        CustomTableColumnSettings.ViewKind viewKind,
        CustomTableColumnSettings columns)
    {
        switch (viewKind)
        {
            case CustomTableColumnSettings.ViewKind.STANDARD:
                settings.StandardCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.UNREGISTERED:
                settings.UnregisteredCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.ZERO_NOTE:
                settings.ZeroNoteCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.PLAYLIST:
                settings.PlaylistCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.FULLSCAN:
                settings.FullScanCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.DUPLICATE:
                settings.DuplicateCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.ENCODING:
                settings.EncodingCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.INSTALL:
                settings.InstallCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR:
                settings.ChartInfoParseErrorCustomTableColumnSettings = columns;
                break;
            case CustomTableColumnSettings.ViewKind.PLAY_HISTORY:
                settings.PlayHistoryCustomTableColumnSettings = columns;
                break;
        }
    }
}

/// <summary>
/// Non-persisted column settings used by the parameterless test construction route.
/// Production composition always supplies <see cref="SettingsMainChartColumnSettingsStore"/>.
/// </summary>
internal sealed class InMemoryMainChartColumnSettingsStore : IMainChartColumnSettingsStore
{
    private readonly Dictionary<CustomTableColumnSettings.ViewKind, CustomTableColumnSettings> mainColumns = new();

    private PlaylistSummaryColumnSettings playlistSummaryColumns;

    public CustomTableColumnSettings GetMain(CustomTableColumnSettings.ViewKind viewKind, bool reset)
    {
        if (reset || !mainColumns.TryGetValue(viewKind, out CustomTableColumnSettings columns))
        {
            columns = new CustomTableColumnSettings(viewKind);
            mainColumns[viewKind] = columns;
        }
        if (viewKind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY)
        {
            columns.EnsurePlayHistoryColumnDefaults();
        }
        return columns;
    }

    public bool IsMainReady(CustomTableColumnSettings.ViewKind viewKind)
    {
        return mainColumns.ContainsKey(viewKind);
    }

    public PlaylistSummaryColumnSettings GetPlaylistSummary(bool ensureCompatibility)
    {
        playlistSummaryColumns ??= new PlaylistSummaryColumnSettings();
        if (ensureCompatibility)
        {
            playlistSummaryColumns.EnsureCompatibility();
        }
        return playlistSummaryColumns;
    }

    public PlaylistSummaryColumnSettings ResetPlaylistSummary()
    {
        playlistSummaryColumns = new PlaylistSummaryColumnSettings();
        playlistSummaryColumns.EnsureCompatibility();
        return playlistSummaryColumns;
    }
}
