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

    /// <summary>局所挿入も全体 sort と同じ比較規則を使い、同値では表の基準順を保ちます。</summary>
    internal static int Compare(PlaylistDetailSourceRow left, PlaylistDetailSourceRow right, PlaylistRequestIdentity identity)
    {
        int compared = CompareSortKeys(left, right, identity);
        return compared == 0 ? left.EntryOrder.CompareTo(right.EntryOrder) : compared;
    }

    /// <summary>列の比較器と題名 tie-break が同値で、明示順も同じときだけ再配置を省略します。</summary>
    internal static bool HaveSameSortInput(PlaylistDetailSourceRow left, PlaylistDetailSourceRow right, PlaylistRequestIdentity identity)
        => left.EntryOrder == right.EntryOrder && CompareSortKeys(left, right, identity) == 0;

    private static int CompareSortKeys(PlaylistDetailSourceRow left, PlaylistDetailSourceRow right, PlaylistRequestIdentity identity)
    {
        string column = string.IsNullOrWhiteSpace(identity.SortColumnName) ? nameof(PlaylistDetailSourceRow.Title) : identity.SortColumnName;
        if (column == "rank") { column = nameof(PlaylistDetailSourceRow.rateDouble); }
        int compared;
        bool natural = column == nameof(PlaylistDetailSourceRow.Folder);
        if (column is "Level" or "level") { compared = Comparer<double?>.Default.Compare(GetLevelKey(left), GetLevelKey(right)); }
        else
        {
            PropertyInfo property = typeof(PlaylistDetailSourceRow).GetProperty(column, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type type = property == null ? null : Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object leftValue = property?.GetValue(left), rightValue = property?.GetValue(right);
            if (natural)
            {
                compared = (identity.SortDirection == ListSortDirection.Descending ? new NaturalComparer(isWhiteSpacePrior: true) : new NaturalComparer())
                    .Compare(leftValue as string ?? string.Empty, rightValue as string ?? string.Empty);
            }
            else if (type == typeof(string) || type == null || !(type.IsEnum || type == typeof(bool) || type == typeof(int) || type == typeof(double) || type == typeof(DateTime)))
            { compared = StringComparer.OrdinalIgnoreCase.Compare(leftValue?.ToString() ?? string.Empty, rightValue?.ToString() ?? string.Empty); }
            else { compared = System.Collections.Comparer.Default.Compare(leftValue, rightValue); }
        }
        if (compared != 0) { return identity.SortDirection == ListSortDirection.Descending ? -Math.Sign(compared) : Math.Sign(compared); }
        return natural ? new NaturalComparer().Compare(GetTitleKey(left), GetTitleKey(right))
            : StringComparer.OrdinalIgnoreCase.Compare(GetTitleKey(left), GetTitleKey(right));
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
            return [.. source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, comparer).ThenBy(row => row.EntryOrder)];
        }
        return [.. source.OrderByDescending(keySelector, comparer).ThenBy(GetTitleKey, comparer).ThenBy(row => row.EntryOrder)];
    }

    private static List<PlaylistDetailSourceRow> SortByLegacyNaturalString(IEnumerable<PlaylistDetailSourceRow> source, Func<PlaylistDetailSourceRow, string> keySelector, ListSortDirection direction)
    {
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector, new NaturalComparer()).ThenBy(GetTitleKey, new NaturalComparer()).ThenBy(row => row.EntryOrder)];
        }
        return [.. source.OrderByDescending(keySelector, new NaturalComparer(isWhiteSpacePrior: true)).ThenBy(GetTitleKey, new NaturalComparer()).ThenBy(row => row.EntryOrder)];
    }

    private static List<PlaylistDetailSourceRow> SortByTypedKey<TKey>(IEnumerable<PlaylistDetailSourceRow> source, Func<PlaylistDetailSourceRow, TKey> keySelector, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, comparer).ThenBy(row => row.EntryOrder)];
        }
        return [.. source.OrderByDescending(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, comparer).ThenBy(row => row.EntryOrder)];
    }

    private static List<PlaylistDetailSourceRow> SortByLevelKey(IEnumerable<PlaylistDetailSourceRow> source, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(row => GetLevelKey(row), Comparer<double?>.Default).ThenBy(GetTitleKey, comparer).ThenBy(row => row.EntryOrder)];
        }
        return [.. source.OrderByDescending(row => GetLevelKey(row), Comparer<double?>.Default).ThenBy(GetTitleKey, comparer).ThenBy(row => row.EntryOrder)];
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
