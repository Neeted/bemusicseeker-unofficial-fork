using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>起動時に決めた三種類のDB読取り方式を、構成の寿命中変更せずに渡します。</summary>
internal sealed class BmsDatabaseLoadOptions
{
    /// <summary>各読取り方式を明示して構築します。falseは従来のSQLite.NET経路を表します。</summary>
    internal BmsDatabaseLoadOptions(bool useRawSongCatalog, bool useRawMaintenanceTable, bool useRawChartInfoHydration)
    {
        UseRawSongCatalog = useRawSongCatalog;
        UseRawMaintenanceTable = useRawMaintenanceTable;
        UseRawChartInfoHydration = useRawChartInfoHydration;
    }

    /// <summary>所持譜面の読取りにraw経路を使うかを取得します。</summary>
    internal bool UseRawSongCatalog { get; }

    /// <summary>保守情報の読取りにraw経路を使うかを取得します。</summary>
    internal bool UseRawMaintenanceTable { get; }

    /// <summary>譜面情報の読取りにraw経路を使うかを取得します。</summary>
    internal bool UseRawChartInfoHydration { get; }

    /// <summary>通常起動の環境を一度捕捉します。sqlite_net以外は従来どおりraw経路です。</summary>
    internal static BmsDatabaseLoadOptions CaptureEnvironment()
        => new(
            !string.Equals(Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE"), "sqlite_net", StringComparison.OrdinalIgnoreCase),
            !string.Equals(Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE"), "sqlite_net", StringComparison.OrdinalIgnoreCase),
            !string.Equals(Environment.GetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE"), "sqlite_net", StringComparison.OrdinalIgnoreCase));
}
