using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

public sealed class PlayHistoryPeriodTreeItem
{
    internal PlayHistoryPeriodTreeItem(string label, PlayHistoryPeriodRequest request, IReadOnlyList<PlayHistoryPeriodTreeItem> children = null)
    {
        Label = label ?? string.Empty;
        Request = request;
        Children = children ?? [];
    }

    public string Label { get; }

    internal PlayHistoryPeriodRequest Request { get; }

    public IReadOnlyList<PlayHistoryPeriodTreeItem> Children { get; }

    public override string ToString()
    {
        return Label;
    }

    internal static IReadOnlyList<PlayHistoryPeriodTreeItem> BuildArchiveTree(IEnumerable<long> playedAtUnixSeconds, TimeZoneInfo timeZone)
    {
        timeZone ??= TimeZoneInfo.Local;
        List<DateTime> localDates = [.. (playedAtUnixSeconds ?? [])
            .Select(value => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(value), timeZone).Date)
            .Distinct()
            .OrderByDescending(value => value)];

        return [.. localDates
            .GroupBy(value => value.Year)
            .OrderByDescending(group => group.Key)
            .Select(yearGroup => new PlayHistoryPeriodTreeItem(
                yearGroup.Key.ToString("0000", CultureInfo.InvariantCulture),
                PlayHistoryPeriodRequest.CreateYear(yearGroup.Key, timeZone),
                [.. yearGroup
                    .GroupBy(value => value.Month)
                    .OrderByDescending(group => group.Key)
                    .Select(monthGroup => new PlayHistoryPeriodTreeItem(
                        yearGroup.Key.ToString("0000", CultureInfo.InvariantCulture) + "/" + monthGroup.Key.ToString("00", CultureInfo.InvariantCulture),
                        PlayHistoryPeriodRequest.CreateMonth(yearGroup.Key, monthGroup.Key, timeZone),
                        [.. monthGroup
                            .OrderByDescending(value => value.Day)
                            .Select(day => new PlayHistoryPeriodTreeItem(
                                day.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
                                PlayHistoryPeriodRequest.CreateDay(day.Year, day.Month, day.Day, timeZone)))]))]))];
    }
}
