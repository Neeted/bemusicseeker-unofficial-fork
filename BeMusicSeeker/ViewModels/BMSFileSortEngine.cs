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
    private static readonly object lockSortKeySelectorCache = new object();

    private static readonly ConcurrentDictionary<string, Func<BMSFile, string>> sortKeySelectorCache = new ConcurrentDictionary<string, Func<BMSFile, string>>(StringComparer.Ordinal);

    /// <summary>
    /// 指定条件で BMS 一覧をソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。null の場合は Title 昇順。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<BMSFile> Sort(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters)
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
        Func<BMSFile, string> sortKeySelector = GetSortKeySelector(columnName);
        if (direction == ListSortDirection.Ascending)
        {
            return safeSource.OrderBy(sortKeySelector, new NaturalComparer<string>()).ThenBy((BMSFile bmsFile) => bmsFile?.Title ?? string.Empty, new NaturalComparer<string>()).ToList();
        }
        return safeSource.OrderByDescending(sortKeySelector, new NaturalComparer<string>(isWhiteSpacePrior: true)).ThenBy((BMSFile bmsFile) => bmsFile?.Title ?? string.Empty, new NaturalComparer<string>()).ToList();
    }

    /// <summary>
    /// カラム名から string キー取得関数を返します。
    /// </summary>
    /// <param name="columnName">BMSFile のプロパティ名。</param>
    /// <returns>ソートキー取得関数。</returns>
    private static Func<BMSFile, string> GetSortKeySelector(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return (BMSFile _) => string.Empty;
        }
        lock (lockSortKeySelectorCache)
        {
            if (sortKeySelectorCache.TryGetValue(columnName, out var value))
            {
                return value;
            }
            PropertyInfo property = typeof(BMSFile).GetProperty(columnName);
            if (property == null)
            {
                Func<BMSFile, string> value2 = (BMSFile _) => string.Empty;
                sortKeySelectorCache[columnName] = value2;
                return value2;
            }
            ParameterExpression parameterExpression = Expression.Parameter(typeof(BMSFile), "row");
            MemberExpression memberExpression = Expression.Property(parameterExpression, property);
            UnaryExpression unaryExpression = Expression.Convert(memberExpression, typeof(object));
            MethodCallExpression methodCallExpression = Expression.Call(typeof(BMSFileSortEngine), "NormalizeSortKey", null, unaryExpression, Expression.Constant(property.PropertyType, typeof(Type)));
            Func<BMSFile, string> func = Expression.Lambda<Func<BMSFile, string>>(methodCallExpression, parameterExpression).Compile();
            sortKeySelectorCache[columnName] = func;
            return func;
        }
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

