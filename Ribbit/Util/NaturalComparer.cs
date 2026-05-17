using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ribbit.Util;

/// <summary>
/// 自然順ソートを行う比較器です。
/// </summary>
public class NaturalComparer<T> : Comparer<string>, IDisposable
{
    private static readonly Regex SplitRegex = new Regex("([+-]?[0-9]+(\\.[0-9]*)?)", RegexOptions.Compiled);

    protected Dictionary<string, string[]> table;

    protected bool isWhiteSpacePrior;

    public NaturalComparer(bool isWhiteSpacePrior = false)
    {
        table = new Dictionary<string, string[]>();
        this.isWhiteSpacePrior = isWhiteSpacePrior;
    }

    public NaturalComparer(bool isWhiteSpacePrior, int initialCapacity)
    {
        table = ((initialCapacity > 0) ? new Dictionary<string, string[]>(initialCapacity) : new Dictionary<string, string[]>());
        this.isWhiteSpacePrior = isWhiteSpacePrior;
    }

    /// <summary>
    /// 互換のために保持されている破棄メソッドです。
    /// </summary>
    public void Dispose()
    {
        table?.Clear();
        table = null;
    }

    /// <summary>
    /// 自然順（数値トークンを数値比較）で文字列を比較します。
    /// </summary>
    /// <param name="x">左辺文字列。</param>
    /// <param name="y">右辺文字列。</param>
    /// <returns>比較結果。</returns>
    public override int Compare(string x, string y)
    {
        if (x == y)
        {
            return 0;
        }
        if (isWhiteSpacePrior)
        {
            if (string.IsNullOrWhiteSpace(x))
            {
                return -1;
            }
            if (string.IsNullOrWhiteSpace(y))
            {
                return 1;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(x))
            {
                return 1;
            }
            if (string.IsNullOrWhiteSpace(y))
            {
                return -1;
            }
        }
        if (!table.TryGetValue(x, out var value))
        {
            value = SplitRegex.Split(x);
            table.Add(x, value);
        }
        if (!table.TryGetValue(y, out var value2))
        {
            value2 = SplitRegex.Split(y);
            table.Add(y, value2);
        }
        for (int i = 0; i < value.Length && i < value2.Length; i++)
        {
            if (value[i] != value2[i])
            {
                return PartCompare(value[i], value2[i]);
            }
        }
        if (value2.Length > value.Length)
        {
            return 1;
        }
        if (value.Length > value2.Length)
        {
            return -1;
        }
        return 0;
    }

    /// <summary>
    /// トークン同士を従来互換ルールで比較します。
    /// </summary>
    /// <param name="left">左辺トークン文字列。</param>
    /// <param name="right">右辺トークン文字列。</param>
    /// <returns>比較結果。</returns>
    protected static int PartCompare(string left, string right)
    {
        if (!double.TryParse(left, out var result))
        {
            return left.CompareTo(right);
        }
        if (!double.TryParse(right, out var result2))
        {
            return left.CompareTo(right);
        }
        return result.CompareTo(result2);
    }
}
