using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BeMusicSeeker.Models;
using Ribbit.Util;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// MainWindow の一覧表示で使用する BMS ソート処理を集約します。
/// </summary>
/// <remarks>
/// 既存挙動互換を最優先し、比較器・キー選択規則を MainWindowViewModel から切り出した実装です。
/// </remarks>
internal static class BMSFileSortEngine
{
    private static readonly ConcurrentDictionary<string, Func<BMSFile, string>> sortKeySelectorCache = new ConcurrentDictionary<string, Func<BMSFile, string>>(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Delegate> typedSortKeySelectorCache = new ConcurrentDictionary<string, Delegate>(StringComparer.Ordinal);

    /// <summary>
    /// DataGrid の実ソートで従来実装を利用するかを示します。
    /// </summary>
    /// <remarks>
    /// 実機性能比較のために、最適化実装を残したまま呼び出し経路だけ切り替えられるようにしています。
    /// </remarks>
    internal static bool UseLegacySortForDataGrid { get; set; } = true;

    /// <summary>
    /// 指定条件で BMS 一覧をソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。null の場合は Title 昇順。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> Sort(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters)
    {
        return Sort(source, sortParameters, isPlaylistDetailView: false, out _);
    }

    /// <summary>
    /// 指定条件で BMS 一覧をソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。null の場合は Title 昇順。</param>
    /// <param name="sortProfile">適用したソートプロファイル。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> Sort(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters, out string sortProfile)
    {
        return Sort(source, sortParameters, isPlaylistDetailView: false, out sortProfile);
    }

    /// <summary>
    /// MainView 用のソートを実行します。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。</param>
    /// <param name="sortProfile">適用したソートプロファイル。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> SortForMainView(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters, bool isPlaylistDetailView, out string sortProfile)
    {
        // NOTE:
        // 現在は実機比較のため MainView だけ従来実装へ戻しています。
        // 互換確認後に false へ戻せば最適化実装を再利用できます。
        if (UseLegacySortForDataGrid)
        {
            return SortByLegacyImplementation(source, sortParameters, out sortProfile);
        }
        return Sort(source, sortParameters, isPlaylistDetailView, out sortProfile);
    }

    /// <summary>
    /// 指定条件で BMS 一覧をソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。null の場合は Title 昇順。</param>
    /// <param name="sortProfile">適用したソートプロファイル。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> Sort(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters, bool isPlaylistDetailView, out string sortProfile)
    {
        IEnumerable<BMSFile> safeSource = source ?? Enumerable.Empty<BMSFile>();
        string columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(BMSFile.Title);
        }
        if (string.Equals(columnName, nameof(BMSFile.rank), StringComparison.Ordinal))
        {
            columnName = nameof(BMSFile.rateDouble);
        }
        if (string.Equals(columnName, nameof(BMSFile.Level), StringComparison.Ordinal) || string.Equals(columnName, nameof(BMSFile.level), StringComparison.Ordinal))
        {
            sortProfile = "level_mixed_double";
            return SortByLevelKey(safeSource, direction);
        }

        PropertyInfo property = typeof(BMSFile).GetProperty(columnName);
        if (property == null)
        {
            sortProfile = "string_fast_fallback";
            Func<BMSFile, string> sortKeySelectorFallback = (BMSFile _) => string.Empty;
            return SortByFastString(safeSource, sortKeySelectorFallback, direction);
        }

        Type propertyType = property.PropertyType;
        Type nonNullableType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (nonNullableType == typeof(string))
        {
            Func<BMSFile, string> stringSortKeySelector = GetSortKeySelector(columnName, property);
            if (string.Equals(columnName, nameof(BMSFile.Folder), StringComparison.Ordinal) && isPlaylistDetailView)
            {
                sortProfile = "folder_natural_legacy";
                return SortByLegacyNaturalString(safeSource, stringSortKeySelector, direction);
            }
            sortProfile = "string_fast_ordinal_ignore_case";
            return SortByFastString(safeSource, stringSortKeySelector, direction);
        }
        if (nonNullableType == typeof(DateTime))
        {
            sortProfile = "date";
            if (propertyType == typeof(DateTime))
            {
                return SortByTypedKey(safeSource, GetTypedSortKeySelector<DateTime>(columnName, property), direction);
            }
            return SortByTypedKey(safeSource, GetTypedSortKeySelector<DateTime?>(columnName, property), direction);
        }

        switch (Type.GetTypeCode(nonNullableType))
        {
            case TypeCode.SByte:
                sortProfile = "numeric_sbyte";
                return (propertyType == typeof(sbyte))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<sbyte>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<sbyte?>(columnName, property), direction);
            case TypeCode.Byte:
                sortProfile = "numeric_byte";
                return (propertyType == typeof(byte))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<byte>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<byte?>(columnName, property), direction);
            case TypeCode.Int16:
                sortProfile = "numeric_int16";
                return (propertyType == typeof(short))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<short>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<short?>(columnName, property), direction);
            case TypeCode.UInt16:
                sortProfile = "numeric_uint16";
                return (propertyType == typeof(ushort))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<ushort>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<ushort?>(columnName, property), direction);
            case TypeCode.Int32:
                sortProfile = "numeric_int32";
                return (propertyType == typeof(int))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<int>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<int?>(columnName, property), direction);
            case TypeCode.UInt32:
                sortProfile = "numeric_uint32";
                return (propertyType == typeof(uint))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<uint>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<uint?>(columnName, property), direction);
            case TypeCode.Int64:
                sortProfile = "numeric_int64";
                return (propertyType == typeof(long))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<long>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<long?>(columnName, property), direction);
            case TypeCode.UInt64:
                sortProfile = "numeric_uint64";
                return (propertyType == typeof(ulong))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<ulong>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<ulong?>(columnName, property), direction);
            case TypeCode.Single:
                sortProfile = "numeric_single";
                return (propertyType == typeof(float))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<float>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<float?>(columnName, property), direction);
            case TypeCode.Double:
                sortProfile = "numeric_double";
                return (propertyType == typeof(double))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<double>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<double?>(columnName, property), direction);
            case TypeCode.Decimal:
                sortProfile = "numeric_decimal";
                return (propertyType == typeof(decimal))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<decimal>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<decimal?>(columnName, property), direction);
            case TypeCode.Boolean:
                sortProfile = "numeric_bool";
                return (propertyType == typeof(bool))
                    ? SortByTypedKey(safeSource, GetTypedSortKeySelector<bool>(columnName, property), direction)
                    : SortByTypedKey(safeSource, GetTypedSortKeySelector<bool?>(columnName, property), direction);
        }

        if (propertyType.IsEnum)
        {
            sortProfile = "enum";
            return SortByTypedKey(safeSource, GetTypedSortKeySelector<int>(columnName, property), direction);
        }

        sortProfile = "string_fast_fallback";
        return SortByFastString(safeSource, GetSortKeySelector(columnName, property), direction);
    }

    /// <summary>
    /// 既存互換の従来ソート実装で並べ替えます。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。</param>
    /// <param name="sortProfile">適用したソートプロファイル。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByLegacyImplementation(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters, out string sortProfile)
    {
        IEnumerable<BMSFile> safeSource = source ?? Enumerable.Empty<BMSFile>();
        string columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(BMSFile.Title);
        }
        if (string.Equals(columnName, nameof(BMSFile.rank), StringComparison.Ordinal))
        {
            columnName = nameof(BMSFile.rateDouble);
        }
        PropertyInfo property = typeof(BMSFile).GetProperty(columnName);
        Func<BMSFile, string> keySelector = delegate(BMSFile row)
        {
            if (row == null || property == null)
            {
                return string.Empty;
            }
            object value = property.GetValue(row);
            if (value == null)
            {
                return string.Empty;
            }
            if (property.PropertyType.IsEnum)
            {
                return ((int)value).ToString();
            }
            return value.ToString() ?? string.Empty;
        };
        if (direction == ListSortDirection.Ascending)
        {
            sortProfile = "legacy_string";
            return safeSource.OrderBy(keySelector, new NaturalComparer<string>()).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new NaturalComparer<string>()).ToList();
        }
        sortProfile = "legacy_string_desc";
        return safeSource.OrderByDescending(keySelector, new NaturalComparer<string>(isWhiteSpacePrior: true)).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new NaturalComparer<string>()).ToList();
    }

    /// <summary>
    /// カラム名から string キー取得関数を返します。
    /// </summary>
    /// <param name="columnName">BMSFile のプロパティ名。</param>
    /// <param name="property">対象プロパティ。</param>
    /// <returns>ソートキー取得関数。</returns>
    private static Func<BMSFile, string> GetSortKeySelector(string columnName, PropertyInfo property)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return (BMSFile _) => string.Empty;
        }
        return sortKeySelectorCache.GetOrAdd(columnName, (string _) =>
        {
            ParameterExpression parameterExpression = Expression.Parameter(typeof(BMSFile), "row");
            MemberExpression memberExpression = Expression.Property(parameterExpression, property);
            if (property.PropertyType == typeof(string))
            {
                // NOTE:
                // path/title 等の文字列カラムは件数が多く、object 変換 + 共通正規化を経由すると
                // キー抽出だけで無視できないコストになるため、null 合体のみの専用 getter を使う。
                BinaryExpression body = Expression.Coalesce(memberExpression, Expression.Constant(string.Empty));
                return Expression.Lambda<Func<BMSFile, string>>(body, parameterExpression).Compile();
            }
            UnaryExpression unaryExpression = Expression.Convert(memberExpression, typeof(object));
            MethodCallExpression methodCallExpression = Expression.Call(typeof(BMSFileSortEngine), "NormalizeSortKey", null, unaryExpression, Expression.Constant(property.PropertyType, typeof(Type)));
            Func<BMSFile, string> func = Expression.Lambda<Func<BMSFile, string>>(methodCallExpression, parameterExpression).Compile();
            return func;
        });
    }

    /// <summary>
    /// カラム名・型に対応する型付きソートキー取得関数を返します。
    /// </summary>
    /// <typeparam name="TKey">ソートキー型。</typeparam>
    /// <param name="columnName">BMSFile のプロパティ名。</param>
    /// <param name="property">対象プロパティ。</param>
    /// <returns>ソートキー取得関数。</returns>
    private static Func<BMSFile, TKey> GetTypedSortKeySelector<TKey>(string columnName, PropertyInfo property)
    {
        string cacheKey = columnName + "|" + typeof(TKey).FullName;
        return (Func<BMSFile, TKey>)typedSortKeySelectorCache.GetOrAdd(cacheKey, delegate
        {
            ParameterExpression parameterExpression = Expression.Parameter(typeof(BMSFile), "row");
            MemberExpression memberExpression = Expression.Property(parameterExpression, property);
            Expression body = memberExpression;
            if (memberExpression.Type != typeof(TKey))
            {
                body = Expression.Convert(memberExpression, typeof(TKey));
            }
            Func<BMSFile, TKey> compiledGetter = Expression.Lambda<Func<BMSFile, TKey>>(body, parameterExpression).Compile();
            return new Func<BMSFile, TKey>((BMSFile row) =>
            {
                if (row == null)
                {
                    return default(TKey);
                }
                return compiledGetter(row);
            });
        });
    }

    /// <summary>
    /// 文字列を高速比較でソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="keySelector">主ソートキー取得関数。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByFastString(IEnumerable<BMSFile> source, Func<BMSFile, string> keySelector, ListSortDirection direction)
    {
        StringComparer stringComparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return source.OrderBy(keySelector, stringComparer).ThenBy(GetTitleKey, stringComparer).ToList();
        }
        return source.OrderByDescending(keySelector, stringComparer).ThenBy(GetTitleKey, stringComparer).ToList();
    }

    /// <summary>
    /// 文字列を従来の自然順比較でソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="keySelector">主ソートキー取得関数。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByLegacyNaturalString(IEnumerable<BMSFile> source, Func<BMSFile, string> keySelector, ListSortDirection direction)
    {
        if (direction == ListSortDirection.Ascending)
        {
            return source.OrderBy(keySelector, new NaturalComparer<string>()).ThenBy(GetTitleKey, new NaturalComparer<string>()).ToList();
        }
        return source.OrderByDescending(keySelector, new NaturalComparer<string>(isWhiteSpacePrior: true)).ThenBy(GetTitleKey, new NaturalComparer<string>()).ToList();
    }

    /// <summary>
    /// 型比較でソートします。
    /// </summary>
    /// <typeparam name="TKey">主ソートキー型。</typeparam>
    /// <param name="source">ソート対象。</param>
    /// <param name="keySelector">主ソートキー取得関数。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByTypedKey<TKey>(IEnumerable<BMSFile> source, Func<BMSFile, TKey> keySelector, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return source.OrderBy(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
        }
        return source.OrderByDescending(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
    }

    /// <summary>
    /// LEVEL 列を画面横断で同一ルール（double?）でソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByLevelKey(IEnumerable<BMSFile> source, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return source.OrderBy(GetLevelKey, Comparer<double?>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
        }
        return source.OrderByDescending(GetLevelKey, Comparer<double?>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
    }

    /// <summary>
    /// LEVEL 列専用の比較キーを返します。
    /// 通常一覧では BMSFile 自身が持つ数値/文字列レベルだけを比較に使います。
    /// </summary>
    /// <param name="bmsFile">対象譜面。</param>
    /// <returns>比較キー。</returns>
    private static double? GetLevelKey(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return null;
        }
        if (bmsFile.level.HasValue)
        {
            return bmsFile.level.Value;
        }
        string levelText = bmsFile.Level;
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

    /// <summary>
    /// タイブレーク用タイトルキーを返します。
    /// </summary>
    /// <param name="bmsFile">対象譜面。</param>
    /// <returns>タイトルキー。</returns>
    private static string GetTitleKey(BMSFile bmsFile)
    {
        return bmsFile?.Title ?? string.Empty;
    }

    /// <summary>
    /// 取得値をソート用の文字列キーへ正規化します。
    /// </summary>
    /// <param name="value">プロパティ値。</param>
    /// <param name="propertyType">プロパティ型。</param>
    /// <returns>比較用キー。</returns>
    private static string NormalizeSortKey(object value, Type propertyType)
    {
        if (value == null)
        {
            return string.Empty;
        }
        if (propertyType != null && propertyType.IsEnum)
        {
            return Convert.ToInt32(value).ToString();
        }
        return value.ToString() ?? string.Empty;
    }

}
