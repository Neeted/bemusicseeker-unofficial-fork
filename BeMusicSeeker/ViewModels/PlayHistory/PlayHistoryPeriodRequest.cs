using System;
using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal enum PlayHistoryPeriodKind
{
    All,
    Today,
    Yesterday,
    Recent7Days,
    Recent30Days,
    Year,
    Month,
    Day,
    Diagnostics
}

/// <summary>ローカル日付の共通開始境界を使い、履歴の半開区間と確定条件を保持する要求です。</summary>
internal sealed class PlayHistoryPeriodRequest
{
    private PlayHistoryPeriodRequest(
        PlayHistoryPeriodKind kind,
        string label,
        long? playedAtFromInclusive,
        long? playedAtToExclusive,
        Lr2PlayHistoryFinalizationFilter finalizationFilter)
    {
        Kind = kind;
        this.label = label ?? string.Empty;
        PlayedAtFromInclusive = playedAtFromInclusive;
        PlayedAtToExclusive = playedAtToExclusive;
        FinalizationFilter = finalizationFilter;
    }

    internal PlayHistoryPeriodKind Kind { get; }

    private readonly string label;

    /// <summary>確定した日時境界を保ち、相対期間のラベルだけを現在言語で取得します。</summary>
    internal string Label => Kind is PlayHistoryPeriodKind.Year or PlayHistoryPeriodKind.Month or PlayHistoryPeriodKind.Day
        ? label : ResolveLabel(Kind);

    internal long? PlayedAtFromInclusive { get; }

    internal long? PlayedAtToExclusive { get; }

    internal Lr2PlayHistoryFinalizationFilter FinalizationFilter { get; }

    internal bool IncludeUnfinalized => FinalizationFilter != Lr2PlayHistoryFinalizationFilter.FinalizedOnly;

    internal static PlayHistoryPeriodRequest All()
    {
        return Create(PlayHistoryPeriodKind.All);
    }

    internal static PlayHistoryPeriodRequest FromTag(string tag)
    {
        if (!Enum.TryParse(tag ?? string.Empty, ignoreCase: true, out PlayHistoryPeriodKind kind))
        {
            kind = PlayHistoryPeriodKind.All;
        }
        return Create(kind);
    }

    internal static PlayHistoryPeriodRequest Create(PlayHistoryPeriodKind kind)
    {
        return Create(kind, DateTimeOffset.Now, TimeZoneInfo.Local);
    }

    /// <summary>指定時刻帯の今日を基準に、相対期間の両端を共通日付境界で求めます。</summary>
    internal static PlayHistoryPeriodRequest Create(PlayHistoryPeriodKind kind, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        timeZone ??= TimeZoneInfo.Local;
        string label = ResolveLabel(kind);
        DateTime localToday = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        long? from = null;
        long? to = null;
        Lr2PlayHistoryFinalizationFilter finalizationFilter = kind == PlayHistoryPeriodKind.Diagnostics
            ? Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
            : Lr2PlayHistoryFinalizationFilter.FinalizedOnly;

        switch (kind)
        {
            case PlayHistoryPeriodKind.Today:
                from = LocalDateBoundary.ToUnixSeconds(localToday, timeZone);
                to = LocalDateBoundary.ToUnixSeconds(localToday.AddDays(1), timeZone);
                break;
            case PlayHistoryPeriodKind.Yesterday:
                from = LocalDateBoundary.ToUnixSeconds(localToday.AddDays(-1), timeZone);
                to = LocalDateBoundary.ToUnixSeconds(localToday, timeZone);
                break;
            case PlayHistoryPeriodKind.Recent7Days:
                from = LocalDateBoundary.ToUnixSeconds(localToday.AddDays(-6), timeZone);
                to = LocalDateBoundary.ToUnixSeconds(localToday.AddDays(1), timeZone);
                break;
            case PlayHistoryPeriodKind.Recent30Days:
                from = LocalDateBoundary.ToUnixSeconds(localToday.AddDays(-29), timeZone);
                to = LocalDateBoundary.ToUnixSeconds(localToday.AddDays(1), timeZone);
                break;
        }

        return new PlayHistoryPeriodRequest(kind, label, from, to, finalizationFilter);
    }

    internal static PlayHistoryPeriodRequest CreateYear(int year, TimeZoneInfo timeZone)
    {
        return CreateRange(
            PlayHistoryPeriodKind.Year,
            year.ToString("0000", CultureInfo.InvariantCulture),
            new DateTime(year, 1, 1),
            new DateTime(year + 1, 1, 1),
            timeZone);
    }

    internal static PlayHistoryPeriodRequest CreateMonth(int year, int month, TimeZoneInfo timeZone)
    {
        DateTime from = new(year, month, 1);
        return CreateRange(
            PlayHistoryPeriodKind.Month,
            year.ToString("0000", CultureInfo.InvariantCulture) + "/" + month.ToString("00", CultureInfo.InvariantCulture),
            from,
            from.AddMonths(1),
            timeZone);
    }

    /// <summary>指定日の開始を含み翌日開始を含まない区間を返します。丸日欠落では両端が同じ空区間です。</summary>
    internal static PlayHistoryPeriodRequest CreateDay(int year, int month, int day, TimeZoneInfo timeZone)
    {
        DateTime from = new(year, month, day);
        return CreateRange(
            PlayHistoryPeriodKind.Day,
            year.ToString("0000", CultureInfo.InvariantCulture) + "/" + month.ToString("00", CultureInfo.InvariantCulture) + "/" + day.ToString("00", CultureInfo.InvariantCulture),
            from,
            from.AddDays(1),
            timeZone);
    }

    internal Lr2PlayHistoryReadRequest ToLr2ReadRequest(string scoreDbPath, bool isLr2LinkedProfile)
    {
        return new Lr2PlayHistoryReadRequest
        {
            ScoreDbPath = scoreDbPath,
            IsLr2LinkedProfile = isLr2LinkedProfile,
            PlayedAtFromInclusive = PlayedAtFromInclusive,
            PlayedAtToExclusive = PlayedAtToExclusive,
            FinalizationFilter = FinalizationFilter
        };
    }

    internal BeatorajaPlayHistoryReadRequest ToBeatorajaReadRequest(string scoreDbPath)
    {
        return new BeatorajaPlayHistoryReadRequest
        {
            ScoreDbPath = scoreDbPath,
            PlayedAtFromInclusive = PlayedAtFromInclusive,
            PlayedAtToExclusive = PlayedAtToExclusive,
            FinalizationFilter = FinalizationFilter
        };
    }

    private static PlayHistoryPeriodRequest CreateRange(PlayHistoryPeriodKind kind, string label, DateTime localFromInclusive, DateTime localToExclusive, TimeZoneInfo timeZone)
    {
        timeZone ??= TimeZoneInfo.Local;
        return new PlayHistoryPeriodRequest(
            kind,
            label,
            LocalDateBoundary.ToUnixSeconds(localFromInclusive, timeZone),
            LocalDateBoundary.ToUnixSeconds(localToExclusive, timeZone),
            Lr2PlayHistoryFinalizationFilter.FinalizedOnly);
    }

    private static string ResolveLabel(PlayHistoryPeriodKind kind)
    {
        return kind switch
        {
            PlayHistoryPeriodKind.Today => BeMusicSeeker.Properties.Resources.Play_history_period_today,
            PlayHistoryPeriodKind.Yesterday => BeMusicSeeker.Properties.Resources.Play_history_period_yesterday,
            PlayHistoryPeriodKind.Recent7Days => BeMusicSeeker.Properties.Resources.Play_history_period_recent_7_days,
            PlayHistoryPeriodKind.Recent30Days => BeMusicSeeker.Properties.Resources.Play_history_period_recent_30_days,
            PlayHistoryPeriodKind.Diagnostics => BeMusicSeeker.Properties.Resources.Play_history_period_diagnostics,
            _ => BeMusicSeeker.Properties.Resources.Play_history_period_all,
        };
    }
}
