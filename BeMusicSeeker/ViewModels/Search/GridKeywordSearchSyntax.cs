using System;
using System.Collections.Generic;
using System.Text;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 検索式の原文範囲と予約構文を識別します。値の正規化や正規表現の生成は行いません。
/// </summary>
internal static class GridKeywordSearchSyntax
{
    /// <summary>引用・エスケープを保持し、引用外の空白で区切った語の原文範囲を返します。</summary>
    internal static IEnumerable<Range> Tokenize(string value)
    {
        bool inQuote = false;
        bool escaping = false;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                }
                else if (c == '\\')
                {
                    escaping = true;
                }
                else if (c == '"')
                {
                    inQuote = false;
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (start < i)
                {
                    yield return start..i;
                }
                start = i + 1;
            }
            else if (c == '"')
            {
                inQuote = true;
            }
        }
        if (start < value.Length)
        {
            yield return start..value.Length;
        }
    }

    /// <summary>否定・ドライブ名・項目・re指定を識別し、語の中の原文範囲を返します。</summary>
    internal static TokenSyntax ParseToken(string token)
    {
        bool isNegated = token.Length > 0 && token[0] == '-';
        int start = isNegated ? 1 : 0;
        string value = token[start..];
        bool isDrive = value.Length >= 2 && value[1] == ':'
            && ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z'));
        int colon = isDrive ? -1 : FindUnquotedChar(value, ':');
        if (colon <= 0)
        {
            return new TokenSyntax(isNegated, null, start..token.Length, false);
        }

        int termsStart = start + colon + 1;
        if (string.Equals(value[..colon].Trim(), "re", StringComparison.OrdinalIgnoreCase))
        {
            return new TokenSyntax(isNegated, null, termsStart..token.Length, true);
        }
        bool isRegex = token.AsSpan(termsStart).StartsWith("re:", StringComparison.OrdinalIgnoreCase);
        return new TokenSyntax(isNegated, start..(start + colon),
            (termsStart + (isRegex ? 3 : 0))..token.Length, isRegex);
    }

    /// <summary>引用外の区切りで分割し、空の選択肢と原文表記を保持します。</summary>
    internal static IEnumerable<string> SplitUnquoted(string value, char separator)
    {
        var builder = new StringBuilder();
        bool inQuote = false;
        bool escaping = false;
        foreach (char c in value ?? string.Empty)
        {
            if (inQuote)
            {
                if (escaping)
                {
                    builder.Append('\\');
                    builder.Append(c);
                    escaping = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaping = true;
                    continue;
                }
                builder.Append(c);
                if (c == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (c == separator)
            {
                yield return builder.ToString();
                builder.Clear();
                continue;
            }
            builder.Append(c);
            if (c == '"')
            {
                inQuote = true;
            }
        }
        if (escaping)
        {
            builder.Append('\\');
        }
        yield return builder.ToString();
    }

    /// <summary>引用内のエスケープを尊重し、引用外にある指定文字の位置を返します。</summary>
    internal static int FindUnquotedChar(string value, char target)
    {
        bool inQuote = false;
        bool escaping = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                }
                else if (c == '\\')
                {
                    escaping = true;
                }
                else if (c == '"')
                {
                    inQuote = false;
                }
            }
            else if (c == '"')
            {
                inQuote = true;
            }
            else if (c == target)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>一語の構文と、項目名・本体の原文範囲を保持する変更不能な識別結果です。</summary>
    /// <param name="IsNegated">先頭に否定記号があるか。</param>
    /// <param name="FieldRange">項目名の範囲。項目指定がない場合はnull。</param>
    /// <param name="TermsRange">通常値または正規表現本体の範囲。</param>
    /// <param name="IsRegex">語全体の選択肢を正規表現として扱うか。</param>
    internal readonly record struct TokenSyntax(bool IsNegated, Range? FieldRange, Range TermsRange, bool IsRegex);
}
