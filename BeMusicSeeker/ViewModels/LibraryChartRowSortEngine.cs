using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using Ribbit.Util;

namespace BeMusicSeeker.ViewModels;

internal readonly struct LibraryChartSortMetrics
{
    internal LibraryChartSortMetrics(
        int rowCount,
        string columnName,
        ListSortDirection direction,
        string propertyTypeName,
        string sortProfile,
        string stringSortKind,
        long sortMs,
        bool sortReuse = false,
        string sortCacheKey = "",
        long sortCacheGeneration = 0L,
        bool sortCacheHit = false,
        long orderCacheLookupMs = 0L,
        long orderBuildMs = 0L)
    {
        RowCount = rowCount;
        ColumnName = columnName;
        Direction = direction;
        PropertyTypeName = propertyTypeName;
        SortProfile = sortProfile;
        StringSortKind = stringSortKind;
        SortMs = sortMs;
        SortReuse = sortReuse;
        SortCacheKey = sortCacheKey ?? string.Empty;
        SortCacheGeneration = sortCacheGeneration;
        SortCacheHit = sortCacheHit;
        OrderCacheLookupMs = orderCacheLookupMs;
        OrderBuildMs = orderBuildMs;
    }

    internal int RowCount { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal string PropertyTypeName { get; }

    internal string SortProfile { get; }

    internal string StringSortKind { get; }

    internal long SortMs { get; }

    internal bool SortReuse { get; }

    internal string SortCacheKey { get; }

    internal long SortCacheGeneration { get; }

    internal bool SortCacheHit { get; }

    internal long OrderCacheLookupMs { get; }

    internal long OrderBuildMs { get; }
}

/// <summary>
/// 通常一覧の BMS / bmson 共通 row をソートします。
/// 既存 BMSFile sort と同じ列名を受け、bmson row を BMSFile 前提から切り離します。
/// </summary>
internal static class LibraryChartRowSortEngine
{
    internal static List<LibraryChartRow> SortForMainView(IEnumerable<LibraryChartRow> source, MainWindowViewModel.cSortParameters sortParameters, bool isPlaylistDetailView, out string sortProfile)
    {
        return SortForMainView(source, sortParameters, isPlaylistDetailView, useLegacySortForDataGrid: true, out sortProfile);
    }

    internal static List<LibraryChartRow> SortForMainView(IEnumerable<LibraryChartRow> source, MainWindowViewModel.cSortParameters sortParameters, bool isPlaylistDetailView, bool useLegacySortForDataGrid, out string sortProfile)
    {
        return SortForMainView(source, sortParameters, isPlaylistDetailView, useLegacySortForDataGrid, out sortProfile, out _);
    }

    internal static List<LibraryChartRow> SortForMainView(IEnumerable<LibraryChartRow> source, MainWindowViewModel.cSortParameters sortParameters, bool isPlaylistDetailView, bool useLegacySortForDataGrid, out string sortProfile, out LibraryChartSortMetrics metrics)
    {
        List<LibraryChartRow> safeSource = source as List<LibraryChartRow> ?? [.. (source ?? [])];
        var stopwatch = Stopwatch.StartNew();
        string columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        string propertyTypeName = "(null)";
        string stringSortKind = "fallback";
        List<LibraryChartRow> sortedRows;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(LibraryChartRow.Title);
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.rank), StringComparison.Ordinal))
        {
            columnName = nameof(LibraryChartRow.rateDouble);
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.Level), StringComparison.Ordinal) || string.Equals(columnName, nameof(LibraryChartRow.level), StringComparison.Ordinal))
        {
            sortProfile = "library_chart_level_mixed_double";
            stringSortKind = "level";
            sortedRows = SortByLevelKey(safeSource, direction);
            metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
            return sortedRows;
        }

        PropertyInfo property = typeof(LibraryChartRow).GetProperty(columnName);
        if (property == null)
        {
            sortProfile = useLegacySortForDataGrid ? "library_chart_legacy_string_fallback" : "library_chart_string_fast_fallback";
            stringSortKind = useLegacySortForDataGrid ? "natural" : "fallback";
            sortedRows = SortByString(safeSource, _ => string.Empty, direction, useLegacySortForDataGrid);
            metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
            return sortedRows;
        }

        Type nonNullableType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        propertyTypeName = property.PropertyType.Name;
        if (property.PropertyType.IsEnum)
        {
            sortProfile = "library_chart_typed";
            stringSortKind = "typed";
            sortedRows = SortByComparable(safeSource, row => property.GetValue(row) as IComparable, direction);
            metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
            return sortedRows;
        }
        if (nonNullableType == typeof(string))
        {
            if (string.Equals(columnName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal) && isPlaylistDetailView)
            {
                sortProfile = "library_chart_folder_natural_legacy";
                stringSortKind = "natural";
                sortedRows = SortByString(safeSource, row => NormalizeSortKey(property.GetValue(row), property.PropertyType), direction, useLegacySort: true);
                metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
                return sortedRows;
            }
            sortProfile = useLegacySortForDataGrid ? "library_chart_legacy_string" : "library_chart_string_fast_ordinal_ignore_case";
            stringSortKind = useLegacySortForDataGrid ? "natural" : "ordinal_ignore_case";
            sortedRows = SortByString(safeSource, row => NormalizeSortKey(property.GetValue(row), property.PropertyType), direction, useLegacySortForDataGrid);
            metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
            return sortedRows;
        }
        if (IsNumericOrDate(nonNullableType))
        {
            sortProfile = "library_chart_typed";
            stringSortKind = "typed";
            sortedRows = SortByComparable(safeSource, row => property.GetValue(row) as IComparable, direction);
            metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
            return sortedRows;
        }

        sortProfile = useLegacySortForDataGrid ? "library_chart_legacy_string_fallback" : "library_chart_string_fast_fallback";
        stringSortKind = useLegacySortForDataGrid ? "natural" : "fallback";
        sortedRows = SortByString(safeSource, row => NormalizeSortKey(property.GetValue(row), property.PropertyType), direction, useLegacySortForDataGrid);
        metrics = CreateMetrics(safeSource.Count, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch);
        return sortedRows;
    }

    private static LibraryChartSortMetrics CreateMetrics(int rowCount, string columnName, ListSortDirection direction, string propertyTypeName, string sortProfile, string stringSortKind, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new LibraryChartSortMetrics(rowCount, columnName, direction, propertyTypeName, sortProfile, stringSortKind, stopwatch.ElapsedMilliseconds);
    }

    private static List<LibraryChartRow> SortByString(IEnumerable<LibraryChartRow> source, Func<LibraryChartRow, string> keySelector, ListSortDirection direction, bool useLegacySort)
    {
        if (useLegacySort)
        {
            NaturalComparer<string> comparer = direction == ListSortDirection.Ascending
                ? new NaturalComparer<string>()
                : new NaturalComparer<string>(isWhiteSpacePrior: true);
            return direction == ListSortDirection.Ascending
                ? [.. source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, new NaturalComparer<string>())]
                : [.. source.OrderByDescending(keySelector, comparer).ThenBy(GetTitleKey, new NaturalComparer<string>())];
        }

        StringComparer comparerFast = StringComparer.OrdinalIgnoreCase;
        return direction == ListSortDirection.Ascending
            ? [.. source.OrderBy(keySelector, comparerFast).ThenBy(GetTitleKey, comparerFast)]
            : [.. source.OrderByDescending(keySelector, comparerFast).ThenBy(GetTitleKey, comparerFast)];
    }

    private static List<LibraryChartRow> SortByComparable(IEnumerable<LibraryChartRow> source, Func<LibraryChartRow, IComparable> keySelector, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        IComparer<IComparable> comparer = Comparer<IComparable>.Create(CompareComparable);
        return direction == ListSortDirection.Ascending
            ? [.. source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, titleComparer)]
            : [.. source.OrderByDescending(keySelector, comparer).ThenBy(GetTitleKey, titleComparer)];
    }

    private static List<LibraryChartRow> SortByLevelKey(IEnumerable<LibraryChartRow> source, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        return direction == ListSortDirection.Ascending
            ? [.. source.OrderBy(GetLevelKey, Comparer<double?>.Default).ThenBy(GetTitleKey, titleComparer)]
            : [.. source.OrderByDescending(GetLevelKey, Comparer<double?>.Default).ThenBy(GetTitleKey, titleComparer)];
    }

    private static double? GetLevelKey(LibraryChartRow row)
    {
        if (row == null)
        {
            return null;
        }
        if (row.level.HasValue)
        {
            return row.level.Value;
        }
        string levelText = row.Level;
        if (string.IsNullOrWhiteSpace(levelText))
        {
            return null;
        }
        if (double.TryParse(levelText, NumberStyles.Float, CultureInfo.CurrentCulture, out double currentCultureValue))
        {
            return currentCultureValue;
        }
        if (double.TryParse(levelText, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantCultureValue))
        {
            return invariantCultureValue;
        }
        return null;
    }

    private static string GetTitleKey(LibraryChartRow row)
    {
        return row?.Title ?? string.Empty;
    }

    private static string NormalizeSortKey(object value, Type propertyType)
    {
        if (value == null)
        {
            return string.Empty;
        }
        if (propertyType != null && propertyType.IsEnum)
        {
            return Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString() ?? string.Empty;
    }

    private static bool IsNumericOrDate(Type type)
    {
        if (type == typeof(DateTime))
        {
            return true;
        }
        return Type.GetTypeCode(type) switch
        {
            TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal or TypeCode.Boolean => true,
            _ => false,
        };
    }

    private static int CompareComparable(IComparable left, IComparable right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return -1;
        }
        if (right == null)
        {
            return 1;
        }
        return left.CompareTo(right);
    }
}
