using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// MainView の実ソートで従来実装を利用するかを示します。
    /// </summary>
    /// <remarks>
    /// 実機性能比較のために、最適化実装を残したまま呼び出し経路だけ切り替えられるようにしています。
    /// </remarks>
    internal static bool UseLegacySortForMainView { get; set; } = true;

    /// <summary>
    /// 指定条件で BMS 一覧をソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。null の場合は Title 昇順。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> Sort(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters)
    {
        return Sort(source, sortParameters, out _);
    }

    /// <summary>
    /// MainView 用のソートを実行します。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。</param>
    /// <param name="sortProfile">適用したソートプロファイル。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> SortForMainView(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters, out string sortProfile)
    {
        // NOTE:
        // 現在は実機比較のため MainView だけ従来実装へ戻しています。
        // 互換確認後に false へ戻せば最適化実装を再利用できます。
        if (UseLegacySortForMainView)
        {
            return SortByLegacyImplementation(source, sortParameters, out sortProfile);
        }
        return Sort(source, sortParameters, out sortProfile);
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
        if (property == null)
        {
            sortProfile = "string_fallback";
            Func<BMSFile, string> sortKeySelectorFallback = (BMSFile _) => string.Empty;
            return SortByNaturalString(safeSource, sortKeySelectorFallback, direction);
        }

        Type propertyType = property.PropertyType;
        Type nonNullableType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (nonNullableType == typeof(string))
        {
            sortProfile = "string_natural";
            return SortByNaturalString(safeSource, GetSortKeySelector(columnName, property), direction);
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

        sortProfile = "string_fallback";
        return SortByNaturalString(safeSource, GetSortKeySelector(columnName, property), direction);
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
    /// 文字列自然順でソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="keySelector">主ソートキー取得関数。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByNaturalString(IEnumerable<BMSFile> source, Func<BMSFile, string> keySelector, ListSortDirection direction)
    {
        int sourceCount = GetSourceCount(source);
        if (direction == ListSortDirection.Ascending)
        {
            // NOTE:
            // 同一ソート内で primary/secondary のキー文字列をキャッシュするため、想定件数を与えて
            // Dictionary の再ハッシュ回数を減らし、20万件規模の自然順比較を軽量化する。
            int comparerCapacity = (sourceCount > 0) ? checked(sourceCount * 2) : 0;
            NaturalComparer<string> comparer = new NaturalComparer<string>(isWhiteSpacePrior: false, comparerCapacity);
            return source.OrderBy(keySelector, comparer).ThenBy(GetTitleKey, comparer).ToList();
        }
        int descendingCapacity = (sourceCount > 0) ? sourceCount : 0;
        NaturalComparer<string> sortComparer = new NaturalComparer<string>(isWhiteSpacePrior: true, descendingCapacity);
        NaturalComparer<string> titleComparer = new NaturalComparer<string>(isWhiteSpacePrior: false, descendingCapacity);
        return source.OrderByDescending(keySelector, sortComparer).ThenBy(GetTitleKey, titleComparer).ToList();
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
        int sourceCount = GetSourceCount(source);
        NaturalComparer<string> titleComparer = new NaturalComparer<string>(isWhiteSpacePrior: false, sourceCount);
        if (direction == ListSortDirection.Ascending)
        {
            return source.OrderBy(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
        }
        return source.OrderByDescending(keySelector, Comparer<TKey>.Default).ThenBy(GetTitleKey, titleComparer).ToList();
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

    /// <summary>
    /// ソート対象件数を取得します。件数不明な列挙は 0 を返します。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <returns>件数、または不明時 0。</returns>
    private static int GetSourceCount(IEnumerable<BMSFile> source)
    {
        if (source is ICollection<BMSFile> collection)
        {
            return collection.Count;
        }
        return 0;
    }
}
