using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>履歴の期間と過去スコアで共有する、ローカル日付の開始境界です。</summary>
internal static class LocalDateBoundary
{
    /// <summary>
    /// 指定日0時に対応する最も早いUTC秒を返します。0時が欠落する場合は、指定日以降の最初の有効な境界を返します。
    /// </summary>
    /// <remarks>
    /// OS提供時刻帯の基準・調整規則のオフセットを逆変換して重複を解決します。
    /// 欠落時は候補オフセットが挟むUTC区間の移行が単独で連続することを前提に二分探索します。
    /// OSデータを超える歴史補正や、任意の人工時刻帯全域の保証は行いません。
    /// </remarks>
    /// <param name="localDate">境界を求めるローカル日付。時刻とKindは使用しません。</param>
    /// <param name="timeZone">この要求内で共有する時刻帯。</param>
    /// <returns>日付開始を含むUTC Unix秒。丸日欠落では翌日と同じ境界になります。</returns>
    internal static long ToUnixSeconds(DateTime localDate, TimeZoneInfo timeZone)
    {
        var midnight = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        HashSet<long> offsets = [timeZone.BaseUtcOffset.Ticks];
        foreach (TimeZoneInfo.AdjustmentRule rule in timeZone.GetAdjustmentRules())
        {
            long standardOffset = (timeZone.BaseUtcOffset + rule.BaseUtcOffsetDelta).Ticks;
            offsets.Add(standardOffset);
            offsets.Add(standardOffset + rule.DaylightDelta.Ticks);
        }

        long earliest = DateTime.MaxValue.Ticks;
        long lower = DateTime.MaxValue.Ticks;
        long upper = DateTime.MinValue.Ticks;
        bool found = false;
        foreach (long offset in offsets)
        {
            long utcTicks = Math.Clamp(midnight.Ticks - offset, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
            lower = Math.Min(lower, utcTicks);
            upper = Math.Max(upper, utcTicks);
            if (TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utcTicks, DateTimeKind.Utc), timeZone) == midnight)
            {
                earliest = Math.Min(earliest, utcTicks);
                found = true;
            }
        }
        if (found)
        {
            return new DateTimeOffset(new DateTime(earliest, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }

        while (lower < upper)
        {
            long middle = lower + (upper - lower) / 2;
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(middle, DateTimeKind.Utc), timeZone);
            if (local < midnight)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        return new DateTimeOffset(new DateTime(lower, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }
}
