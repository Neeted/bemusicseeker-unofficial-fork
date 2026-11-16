using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 整形済みの保存式を、通常部分は大小文字を無視し、構文上の正規表現本体は原文で比較します。
/// 不正な正規表現も保持するため、検索条件の解析・正規表現生成には依存しません。
/// </summary>
internal sealed class KeywordSearchSavedQueryComparer : IEqualityComparer<string>
{
    /// <summary>履歴・お気に入りの追加、削除、復元、重複表示に共用する比較規則です。</summary>
    internal static KeywordSearchSavedQueryComparer Instance { get; } = new();

    private KeywordSearchSavedQueryComparer() { }

    /// <summary>正規表現本体の位置と原文、およびその他の文字列を対称に比較します。</summary>
    public bool Equals(string x, string y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }
        if (x is null || y is null || x.Length != y.Length)
        {
            return false;
        }
        using IEnumerator<Range> left = GetRegexRanges(x).GetEnumerator();
        using IEnumerator<Range> right = GetRegexRanges(y).GetEnumerator();
        int start = 0;
        while (left.MoveNext())
        {
            if (!right.MoveNext() || !left.Current.Equals(right.Current))
            {
                return false;
            }
            Range regex = left.Current;
            if (!x.AsSpan(start, regex.Start.Value - start).Equals(
                    y.AsSpan(start, regex.Start.Value - start), StringComparison.OrdinalIgnoreCase)
                || !x.AsSpan(regex).Equals(y.AsSpan(regex), StringComparison.Ordinal))
            {
                return false;
            }
            start = regex.End.Value;
        }
        return !right.MoveNext()
            && x.AsSpan(start).Equals(y.AsSpan(start), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>等値比較と同じ原文区間・大小文字規則からハッシュ値を求めます。</summary>
    public int GetHashCode(string obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        HashCode hash = new();
        int start = 0;
        foreach (Range regex in GetRegexRanges(obj))
        {
            hash.Add(string.GetHashCode(obj.AsSpan(start, regex.Start.Value - start), StringComparison.OrdinalIgnoreCase));
            hash.Add(string.GetHashCode(obj.AsSpan(regex), StringComparison.Ordinal));
            start = regex.End.Value;
        }
        hash.Add(string.GetHashCode(obj.AsSpan(start), StringComparison.OrdinalIgnoreCase));
        return hash.ToHashCode();
    }

    private static IEnumerable<Range> GetRegexRanges(string value)
    {
        foreach (Range tokenRange in GridKeywordSearchSyntax.Tokenize(value))
        {
            GridKeywordSearchSyntax.TokenSyntax syntax = GridKeywordSearchSyntax.ParseToken(value[tokenRange]);
            if (syntax.IsRegex)
            {
                yield return (tokenRange.Start.Value + syntax.TermsRange.Start.Value)..(tokenRange.Start.Value + syntax.TermsRange.End.Value);
            }
        }
    }
}
