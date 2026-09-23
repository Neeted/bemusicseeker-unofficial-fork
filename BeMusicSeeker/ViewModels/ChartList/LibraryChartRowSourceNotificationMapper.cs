using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 表示 row が購読する storage owner の PropertyChanged を chart 表示単位へ写像します。
/// BMS storage row の property 名を row 本体へ広げないための境界です。
/// </summary>
[Flags]
internal enum LibraryChartRowSourceNotificationGroups
{
    /// <summary>
    /// 表示 group へ展開しない通知です。
    /// </summary>
    None = 0,

    /// <summary>
    /// WARNING 表示に関係する通知です。
    /// </summary>
    WarningPresentation = 1,

    /// <summary>
    /// score / ranking 表示列に関係する通知です。
    /// </summary>
    ScoreDisplay = 2,

    /// <summary>
    /// maintenance / resource health 表示列に関係する通知です。
    /// </summary>
    MaintenanceDisplay = 4,

    /// <summary>
    /// 全表示 group を再通知する必要がある通知です。
    /// </summary>
    All = WarningPresentation | ScoreDisplay | MaintenanceDisplay
}

/// <summary>
/// BMS storage row の通知名を chart row の表示更新単位へ変換します。
/// </summary>
internal static class LibraryChartRowSourceNotificationMapper
{
    /// <summary>
    /// BMS storage row の property 名から、更新が必要な chart row 表示 group を返します。
    /// </summary>
    /// <param name="propertyName">BMS storage row が通知した property 名。</param>
    /// <returns>更新が必要な chart row 表示 group。</returns>
    internal static LibraryChartRowSourceNotificationGroups MapBmsStorageProperty(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return LibraryChartRowSourceNotificationGroups.All;
        }
        if (string.Equals(propertyName, nameof(BMSFile.Warnings), StringComparison.Ordinal))
        {
            return LibraryChartRowSourceNotificationGroups.WarningPresentation;
        }
        if (string.Equals(propertyName, nameof(BMSFile.bmsScore), StringComparison.Ordinal))
        {
            return LibraryChartRowSourceNotificationGroups.ScoreDisplay;
        }
        if (string.Equals(propertyName, nameof(BMSFile.maintenanceInfo), StringComparison.Ordinal))
        {
            return LibraryChartRowSourceNotificationGroups.MaintenanceDisplay;
        }
        return LibraryChartRowSourceNotificationGroups.None;
    }
}
