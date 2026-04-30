using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using Ribbit.Util;

namespace BeMusicSeeker.ViewModels;

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
        IEnumerable<LibraryChartRow> safeSource = source ?? Enumerable.Empty<LibraryChartRow>();
        string columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
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
            return SortByLevelKey(safeSource, direction);
        }

        PropertyInfo property = typeof(LibraryChartRow).GetProperty(columnName);
        if (property == null)
        {
            sortProfile = useLegacySortForDataGrid ? "library_chart_legacy_string_fallback" : "library_chart_string_fast_fallback";
            return SortByString(safeSource, _ => string.Empty, direction, useLegacySortForDataGrid);
        }

        Type nonNullableType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (nonNullableType == typeof(string) || property.PropertyType.IsEnum)
        {
            if (string.Equals(columnName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal) && isPlaylistDetailView)
            {
                sortProfile = "library_chart_folder_natural_legacy";
                return SortByString(safeSource, row => NormalizeSortKey(property.GetValue(row), property.PropertyType), direction, useLegacySort: true);
            }
            sortProfile = useLegacySortForDataGrid ? "library_chart_legacy_string" : "library_chart_string_fast_ordinal_ignore_case";
            return SortByString(safeSource, row => NormalizeSortKey(property.GetValue(row), property.PropertyType), direction, useLegacySortForDataGrid);
        }
        if (IsNumericOrDate(nonNullableType))
        {
            sortProfile = "library_chart_typed";
            return SortByComparable(safeSource, row => property.GetValue(row) as IComparable, direction);
        }

        sortProfile = useLegacySortForDataGrid ? "library_chart_legacy_string_fallback" : "library_chart_string_fast_fallback";
        return SortByString(safeSource, row => NormalizeSortKey(property.GetValue(row), property.PropertyType), direction, useLegacySortForDataGrid);
    }

    private static List<LibraryChartRow> SortByString(IEnumerable<LibraryChartRow> source, Func<LibraryChartRow, string> keySelector, ListSortDirection direction, bool useLegacySort)
    {
        if (useLegacySort)
        {
            NaturalComparer<string> comparer = direction == ListSortDirection.Ascending
                ? new NaturalComparer<string>()
                : new NaturalComparer<string>(isWhiteSpacePrior: true);
            return direction == ListSortDirection.Ascending
                ? source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, new NaturalComparer<string>()).ToList()
                : source.OrderByDescending(keySelector, comparer).ThenBy(GetTitleKey, new NaturalComparer<string>()).ToList();
        }

        StringComparer comparerFast = StringComparer.OrdinalIgnoreCase;
        return direction == ListSortDirection.Ascending
            ? source.OrderBy(keySelector, comparerFast).ThenBy(GetTitleKey, comparerFast).ToList()
            : source.OrderByDescending(keySelector, comparerFast).ThenBy(GetTitleKey, comparerFast).ToList();
    }

    private static List<LibraryChartRow> SortByComparable(IEnumerable<LibraryChartRow> source, Func<LibraryChartRow, IComparable> keySelector, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        IComparer<IComparable> comparer = Comparer<IComparable>.Create(CompareComparable);
        return direction == ListSortDirection.Ascending
            ? source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, titleComparer).ToList()
            : source.OrderByDescending(keySelector, comparer).ThenBy(GetTitleKey, titleComparer).ToList();
    }

    private static List<LibraryChartRow> SortByLevelKey(IEnumerable<LibraryChartRow> source, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        return direction == ListSortDirection.Ascending
            ? source.OrderBy(GetLevelKey, Comparer<double?>.Default).ThenBy(GetTitleKey, titleComparer).ToList()
            : source.OrderByDescending(GetLevelKey, Comparer<double?>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
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
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.SByte:
            case TypeCode.Byte:
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Single:
            case TypeCode.Double:
            case TypeCode.Decimal:
            case TypeCode.Boolean:
                return true;
            default:
                return false;
        }
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
