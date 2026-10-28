using System;

namespace BeMusicSeeker.Tests;

/// <summary>ホスト設定を変えずに日付境界の欠落・重複を観測するための固定時刻帯です。</summary>
internal static class LocalDateBoundaryTestTimeZones
{
    /// <summary>通常日、0時欠落、前日からの欠落、0時重複、丸日欠落の順に固定時刻帯を返します。</summary>
    internal static TimeZoneInfo Create(int scenario)
    {
        if (scenario == 0)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Boundary+09", TimeSpan.FromHours(9), "Boundary+09", "Boundary+09");
        }
        if (scenario == 4)
        {
            var skippedDay = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2024, 6, 1), new DateTime(2024, 12, 31), TimeSpan.Zero,
                Transition(1, 1, 0, 0), Transition(12, 31, 0, 0), TimeSpan.FromHours(24));
            return TimeZoneInfo.CreateCustomTimeZone("BoundarySkippedDay", TimeSpan.FromHours(-12),
                "BoundarySkippedDay", "BoundarySkippedDay", "BoundarySkippedDay", [skippedDay]);
        }

        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), TimeSpan.FromMinutes(30),
            scenario == 2 ? Transition(3, 1, 23, 50) : Transition(3, 1, 0, 0),
            Transition(10, 1, 0, 30));
        string id = "BoundaryHalfHour" + scenario;
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.Zero, id, id, id, [rule]);
    }

    private static TimeZoneInfo.TransitionTime Transition(int month, int day, int hour, int minute)
    {
        return TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, hour, minute, 0), month, day);
    }
}
