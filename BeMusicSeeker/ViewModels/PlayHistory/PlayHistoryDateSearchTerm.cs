using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Represents a displayed Play Log timestamp at whole-second precision without an instant,
/// offset, or <see cref="DateTimeKind"/>.
/// </summary>
internal readonly struct PlayHistoryWallClockSecond : IComparable<PlayHistoryWallClockSecond>, IEquatable<PlayHistoryWallClockSecond>
{
    private readonly int year;
    private readonly int month;
    private readonly int day;
    private readonly int hour;
    private readonly int minute;
    private readonly int second;

    private PlayHistoryWallClockSecond(int year, int month, int day, int hour, int minute, int second)
    {
        _ = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        this.year = year;
        this.month = month;
        this.day = day;
        this.hour = hour;
        this.minute = minute;
        this.second = second;
    }

    /// <summary>
    /// Gets the four-digit calendar year component.
    /// </summary>
    internal int Year => year;

    /// <summary>
    /// Gets the calendar month component.
    /// </summary>
    internal int Month => month;

    /// <summary>
    /// Gets the calendar day component.
    /// </summary>
    internal int Day => day;

    /// <summary>
    /// Gets the hour component.
    /// </summary>
    internal int Hour => hour;

    /// <summary>
    /// Gets the minute component.
    /// </summary>
    internal int Minute => minute;

    /// <summary>
    /// Gets the second component.
    /// </summary>
    internal int Second => second;

    /// <summary>
    /// Projects Unix-second data into the displayed wall-clock components for a specific time zone.
    /// </summary>
    /// <param name="unixSeconds">The source Unix timestamp in whole seconds.</param>
    /// <param name="timeZone">The time zone used only for the projection.</param>
    /// <returns>The projected year, month, day, hour, minute, and second.</returns>
    internal static PlayHistoryWallClockSecond FromUnixSeconds(long unixSeconds, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        DateTime utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        DateTime displayed = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
        return FromDateTime(displayed);
    }

    /// <summary>
    /// Projects a displayed date value while deliberately ignoring its kind and sub-second ticks.
    /// </summary>
    /// <param name="value">The displayed date value whose calendar components are used.</param>
    /// <returns>The value's year, month, day, hour, minute, and second.</returns>
    internal static PlayHistoryWallClockSecond FromDateTime(DateTime value)
    {
        return new PlayHistoryWallClockSecond(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second);
    }

    /// <summary>
    /// Creates an unspecified <see cref="DateTime"/> containing these display components.
    /// </summary>
    /// <returns>A date value with no offset or local-time ambiguity metadata.</returns>
    internal DateTime ToDateTime()
    {
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Formats this value using the canonical Play Log timestamp representation.
    /// </summary>
    /// <returns>The invariant <c>yyyy/MM/dd HH:mm:ss</c> representation.</returns>
    internal string ToCanonicalTimestamp()
    {
        return year.ToString("D4", CultureInfo.InvariantCulture)
            + "/" + month.ToString("D2", CultureInfo.InvariantCulture)
            + "/" + day.ToString("D2", CultureInfo.InvariantCulture)
            + " " + hour.ToString("D2", CultureInfo.InvariantCulture)
            + ":" + minute.ToString("D2", CultureInfo.InvariantCulture)
            + ":" + second.ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public int CompareTo(PlayHistoryWallClockSecond other)
    {
        int comparison = year.CompareTo(other.year);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = month.CompareTo(other.month);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = day.CompareTo(other.day);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = hour.CompareTo(other.hour);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = minute.CompareTo(other.minute);
        return comparison != 0 ? comparison : second.CompareTo(other.second);
    }

    /// <inheritdoc />
    public bool Equals(PlayHistoryWallClockSecond other)
    {
        return year == other.year
            && month == other.month
            && day == other.day
            && hour == other.hour
            && minute == other.minute
            && second == other.second;
    }

    /// <inheritdoc />
    public override bool Equals(object obj)
    {
        return obj is PlayHistoryWallClockSecond other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(year, month, day, hour, minute, second);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return ToCanonicalTimestamp();
    }
}

/// <summary>
/// Owns local wall-clock date search syntax and the immutable range snapshot used by Play Log actions.
/// </summary>
internal sealed class PlayHistoryDateSearchTerm
{
    private const string LocalTimestampFormat = "yyyy/MM/dd HH:mm:ss";

    private static readonly string[] ExactDayFormats =
    [
        "yyyy-MM-dd",
        "yyyy/M/d",
        "yyyy/MM/dd",
        "yyyyMMdd"
    ];

    private PlayHistoryDateSearchTerm(PlayHistoryWallClockSecond start, PlayHistoryWallClockSecond end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Gets the inclusive local wall-clock start of this term.
    /// </summary>
    internal PlayHistoryWallClockSecond Start { get; }

    /// <summary>
    /// Gets the inclusive local wall-clock end of this term.
    /// </summary>
    internal PlayHistoryWallClockSecond End { get; }

    /// <summary>
    /// Gets the canonical closed-range clause emitted for a selection snapshot.
    /// </summary>
    internal string Clause =>
        $"date:\"{Start.ToCanonicalTimestamp()}..{End.ToCanonicalTimestamp()}\"";

    /// <summary>
    /// Parses one dedicated Play Log date term without applying timezone or offset conversion.
    /// </summary>
    /// <param name="term">The normalized field term.</param>
    /// <param name="parsed">The parsed local wall-clock term when successful.</param>
    /// <returns><see langword="true"/> when the term is a supported exact day, timestamp, or closed range.</returns>
    internal static bool TryParse(string term, out PlayHistoryDateSearchTerm parsed)
    {
        parsed = null;
        string text = term?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        const string delimiter = "..";
        int delimiterIndex = text.IndexOf(delimiter, StringComparison.Ordinal);
        if (delimiterIndex >= 0)
        {
            if (text.IndexOf(delimiter, delimiterIndex + delimiter.Length, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            string startText = text[..delimiterIndex];
            string endText = text[(delimiterIndex + delimiter.Length)..];
            if (!TryParseLocalTimestamp(startText, out PlayHistoryWallClockSecond start)
                || !TryParseLocalTimestamp(endText, out PlayHistoryWallClockSecond end)
                || start.CompareTo(end) > 0)
            {
                return false;
            }

            parsed = new PlayHistoryDateSearchTerm(start, end);
            return true;
        }

        if (TryParseLocalTimestamp(text, out PlayHistoryWallClockSecond timestamp))
        {
            parsed = new PlayHistoryDateSearchTerm(timestamp, timestamp);
            return true;
        }

        if (!DateTime.TryParseExact(
                text,
                ExactDayFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime day))
        {
            return false;
        }

        var dayStart = PlayHistoryWallClockSecond.FromDateTime(day.Date);
        var dayEnd = PlayHistoryWallClockSecond.FromDateTime(
            day.Date.AddHours(23).AddMinutes(59).AddSeconds(59));
        parsed = new PlayHistoryDateSearchTerm(dayStart, dayEnd);
        return true;
    }

    /// <summary>
    /// Captures the minimum and maximum displayed PlayedAt values from a multi-row selection.
    /// </summary>
    /// <param name="rows">The selected Play Log rows in their current table order.</param>
    /// <param name="snapshot">The immutable local wall-clock range snapshot.</param>
    /// <returns><see langword="true"/> only when at least two Play Log rows are selected.</returns>
    internal static bool TryCreate(IEnumerable<PlayHistoryRow> rows, out PlayHistoryDateSearchTerm snapshot)
    {
        snapshot = null;
        PlayHistoryWallClockSecond[] playedAt = [.. (rows ?? [])
            .Where(row => row != null)
            .Select(row => row.PlayedAtWallClockSecond)];
        if (playedAt.Length < 2)
        {
            return false;
        }

        snapshot = new PlayHistoryDateSearchTerm(playedAt.Min(), playedAt.Max());
        return true;
    }

    /// <summary>
    /// Tests one typed Play Log wall-clock value against this inclusive range.
    /// </summary>
    /// <param name="playedAt">The displayed Play Log wall-clock second.</param>
    /// <returns><see langword="true"/> when the value lies within the closed range.</returns>
    internal bool Matches(PlayHistoryWallClockSecond playedAt)
    {
        return playedAt.CompareTo(Start) >= 0 && playedAt.CompareTo(End) <= 0;
    }

    /// <summary>
    /// Appends this canonical clause while preserving the caller's keyword text exactly.
    /// </summary>
    /// <param name="keywordFilter">The existing keyword filter.</param>
    /// <returns>The existing filter followed by this term using the contract's spacing rule.</returns>
    internal string AppendTo(string keywordFilter)
    {
        string existing = keywordFilter ?? string.Empty;
        if (existing.Length == 0)
        {
            return Clause;
        }

        return char.IsWhiteSpace(existing[^1])
            ? existing + Clause
            : existing + " " + Clause;
    }

    private static bool TryParseLocalTimestamp(string text, out PlayHistoryWallClockSecond timestamp)
    {
        if (!DateTime.TryParseExact(
                text ?? string.Empty,
                LocalTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            timestamp = default;
            return false;
        }

        timestamp = PlayHistoryWallClockSecond.FromDateTime(parsed);
        return true;
    }
}
