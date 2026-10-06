using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Ribbit.Util;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// playlist 詳細表示用 source row のソート処理を集約します。
/// </summary>
internal static class PlaylistDetailSortEngine
{
    private static readonly ConcurrentDictionary<string, Func<PlaylistDetailSourceRow, string>> stringSelectorCache = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Delegate> typedSelectorCache = new(StringComparer.Ordinal);

    /// <summary>
    /// source row を指定条件でソートします。
    /// </summary>
    internal static List<PlaylistDetailSourceRow> Sort(IEnumerable<PlaylistDetailSourceRow> source, ChartListSortParameters sortParameters, out string sortProfile)
    {
        IEnumerable<PlaylistDetailSourceRow> safeSource = source ?? [];
        string columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(PlaylistDetailSourceRow.Title);
        }
        if (string.Equals(columnName, "rank", StringComparison.Ordinal))
        {
            columnName = nameof(PlaylistDetailSourceRow.rateDouble);
        }
        if (string.Equals(columnName, nameof(PlaylistDetailRow.Level), StringComparison.Ordinal) || string.Equals(columnName, "level", StringComparison.Ordinal))
        {
            sortProfile = "level_mixed_double";
            return SortByLevelKey(safeSource, direction);
        }

        PropertyInfo property = typeof(PlaylistDetailSourceRow).GetProperty(columnName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null)
        {
            sortProfile = "string_fast_fallback";
            return SortByFastString(safeSource, _ => string.Empty, direction);
        }
        Type propertyType = property.PropertyType;
        Type nonNullableType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (nonNullableType == typeof(string))
        {
            Func<PlaylistDetailSourceRow, string> keySelector = GetStringSelector(columnName, property);
            if (string.Equals(columnName, nameof(PlaylistDetailSourceRow.Folder), StringComparison.Ordinal))
            {
                sortProfile = "folder_natural_legacy";
                return SortByLegacyNaturalString(safeSource, keySelector, direction);
            }
            sortProfile = "string_fast_ordinal_ignore_case";
            return SortByFastString(safeSource, keySelector, direction);
        }
        if (nonNullableType == typeof(DateTime))
        {
            sortProfile = "date";
            return propertyType == typeof(DateTime)
                ? SortByTypedKey(safeSource, GetTypedSelector<DateTime>(columnName, property), direction)
                : SortByTypedKey(safeSource, GetTypedSelector<DateTime?>(columnName, property), direction);
        }
        if (propertyType.IsEnum)
        {
            sortProfile = "enum";
            return SortByTypedKey(safeSource, GetTypedSelector<int>(columnName, property), direction);
        }

        switch (Type.GetTypeCode(nonNullableType))
        {
            case TypeCode.Boolean:
                sortProfile = "numeric_bool";
                return propertyType == typeof(bool)
                    ? SortByTypedKey(safeSource, GetTypedSelector<bool>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSelector<bool?>(columnName, property), direction);
            case TypeCode.Int32:
                sortProfile = "numeric_int32";
                return propertyType == typeof(int)
                    ? SortByTypedKey(safeSource, GetTypedSelector<int>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSelector<int?>(columnName, property), direction);
            case TypeCode.Double:
                sortProfile = "numeric_double";
                return propertyType == typeof(double)
                    ? SortByTypedKey(safeSource, GetTypedSelector<double>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSelector<double?>(columnName, property), direction);
            case TypeCode.String:
                break;
        }

        sortProfile = "string_fast_fallback";
        return SortByFastString(safeSource, GetStringSelector(columnName, property), direction);
    }

    private static Func<PlaylistDetailSourceRow, string> GetStringSelector(string columnName, PropertyInfo property)
    {
        return stringSelectorCache.GetOrAdd(columnName, delegate
        {
            ParameterExpression parameter = Expression.Parameter(typeof(PlaylistDetailSourceRow), "row");
            MemberExpression member = Expression.Property(parameter, property);
            Expression body = property.PropertyType == typeof(string)
                ? Expression.Coalesce(member, Expression.Constant(string.Empty))
                : Expression.Call(member, property.PropertyType.GetMethod(nameof(ToString), Type.EmptyTypes));
            return Expression.Lambda<Func<PlaylistDetailSourceRow, string>>(body, parameter).Compile();
        });
    }

    private static Func<PlaylistDetailSourceRow, TKey> GetTypedSelector<TKey>(string columnName, PropertyInfo property)
    {
        string cacheKey = columnName + "|" + typeof(TKey).FullName;
        return (Func<PlaylistDetailSourceRow, TKey>)typedSelectorCache.GetOrAdd(cacheKey, delegate
        {
            ParameterExpression parameter = Expression.Parameter(typeof(PlaylistDetailSourceRow), "row");
            MemberExpression member = Expression.Property(parameter, property);
            Expression body = member.Type == typeof(TKey) ? (Expression)member : Expression.Convert(member, typeof(TKey));
            Func<PlaylistDetailSourceRow, TKey> compiled = Expression.Lambda<Func<PlaylistDetailSourceRow, TKey>>(body, parameter).Compile();
            return new Func<PlaylistDetailSourceRow, TKey>(row => row == null ? default : compiled(row));
        });
    }

    private static List<PlaylistDetailSourceRow> SortByFastString(IEnumerable<PlaylistDetailSourceRow> source, Func<PlaylistDetailSourceRow, string> keySelector, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, comparer)];
        }
        return [.. source.OrderByDescending(keySelector, comparer).ThenBy(GetTitleKey, comparer)];
    }

    private static List<PlaylistDetailSourceRow> SortByLegacyNaturalString(IEnumerable<PlaylistDetailSourceRow> source, Func<PlaylistDetailSourceRow, string> keySelector, ListSortDirection direction)
    {
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector, new NaturalComparer()).ThenBy(GetTitleKey, new NaturalComparer())];
        }
        return [.. source.OrderByDescending(keySelector, new NaturalComparer(isWhiteSpacePrior: true)).ThenBy(GetTitleKey, new NaturalComparer())];
    }

    private static List<PlaylistDetailSourceRow> SortByTypedKey<TKey>(IEnumerable<PlaylistDetailSourceRow> source, Func<PlaylistDetailSourceRow, TKey> keySelector, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, comparer)];
        }
        return [.. source.OrderByDescending(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, comparer)];
    }

    private static List<PlaylistDetailSourceRow> SortByLevelKey(IEnumerable<PlaylistDetailSourceRow> source, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(row => GetLevelKey(row), Comparer<double?>.Default).ThenBy(GetTitleKey, comparer)];
        }
        return [.. source.OrderByDescending(row => GetLevelKey(row), Comparer<double?>.Default).ThenBy(GetTitleKey, comparer)];
    }

    private static double? GetLevelKey(PlaylistDetailSourceRow row)
    {
        if (row == null)
        {
            return null;
        }
        if (row.EntryLevelSortKey.HasValue)
        {
            return row.EntryLevelSortKey.Value;
        }
        if (double.TryParse(row.Level, NumberStyles.Float, CultureInfo.CurrentCulture, out double currentCultureValue))
        {
            return currentCultureValue;
        }
        if (double.TryParse(row.Level, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantCultureValue))
        {
            return invariantCultureValue;
        }
        return null;
    }

    private static string GetTitleKey(PlaylistDetailSourceRow row)
    {
        return row?.Title ?? string.Empty;
    }
}
