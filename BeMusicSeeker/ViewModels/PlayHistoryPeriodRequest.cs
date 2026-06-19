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

internal sealed class PlayHistoryPeriodRequest
{
    private PlayHistoryPeriodRequest(
        PlayHistoryPeriodKind kind,
        string label,
        long? playedAtFromInclusive,
        long? playedAtToExclusive,
        bool includeUnfinalized)
    {
        Kind = kind;
        Label = label ?? string.Empty;
        PlayedAtFromInclusive = playedAtFromInclusive;
        PlayedAtToExclusive = playedAtToExclusive;
        IncludeUnfinalized = includeUnfinalized;
    }

    internal PlayHistoryPeriodKind Kind { get; }

    internal string Label { get; }

    internal long? PlayedAtFromInclusive { get; }

    internal long? PlayedAtToExclusive { get; }

    internal bool IncludeUnfinalized { get; }

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

    internal static PlayHistoryPeriodRequest Create(PlayHistoryPeriodKind kind, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        timeZone ??= TimeZoneInfo.Local;
        string label = ResolveLabel(kind);
        DateTime localToday = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        long? from = null;
        long? to = null;
        bool includeUnfinalized = kind == PlayHistoryPeriodKind.Diagnostics;

        switch (kind)
        {
            case PlayHistoryPeriodKind.Today:
                from = ToUnixSeconds(localToday, timeZone);
                to = ToUnixSeconds(localToday.AddDays(1), timeZone);
                break;
            case PlayHistoryPeriodKind.Yesterday:
                from = ToUnixSeconds(localToday.AddDays(-1), timeZone);
                to = ToUnixSeconds(localToday, timeZone);
                break;
            case PlayHistoryPeriodKind.Recent7Days:
                from = ToUnixSeconds(localToday.AddDays(-6), timeZone);
                to = ToUnixSeconds(localToday.AddDays(1), timeZone);
                break;
            case PlayHistoryPeriodKind.Recent30Days:
                from = ToUnixSeconds(localToday.AddDays(-29), timeZone);
                to = ToUnixSeconds(localToday.AddDays(1), timeZone);
                break;
        }

        return new PlayHistoryPeriodRequest(kind, label, from, to, includeUnfinalized);
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
            IncludeUnfinalized = IncludeUnfinalized
        };
    }

    internal BeatorajaPlayHistoryReadRequest ToBeatorajaReadRequest(string scoreDbPath)
    {
        return new BeatorajaPlayHistoryReadRequest
        {
            ScoreDbPath = scoreDbPath,
            PlayedAtFromInclusive = PlayedAtFromInclusive,
            PlayedAtToExclusive = PlayedAtToExclusive
        };
    }

    private static long ToUnixSeconds(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        DateTime unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone)).ToUnixTimeSeconds();
    }

    private static PlayHistoryPeriodRequest CreateRange(PlayHistoryPeriodKind kind, string label, DateTime localFromInclusive, DateTime localToExclusive, TimeZoneInfo timeZone)
    {
        timeZone ??= TimeZoneInfo.Local;
        return new PlayHistoryPeriodRequest(
            kind,
            label,
            ToUnixSeconds(localFromInclusive, timeZone),
            ToUnixSeconds(localToExclusive, timeZone),
            includeUnfinalized: false);
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
