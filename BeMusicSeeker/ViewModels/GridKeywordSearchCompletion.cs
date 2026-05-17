using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 検索欄の候補種類を表します。
/// field 補完と履歴は適用範囲が異なるため、UI 側で同じ候補一覧として扱えるよう区別します。
/// </summary>
public enum KeywordSearchSuggestionKind
{
    /// <summary>
    /// 現在の token 内 field prefix を置換する候補です。
    /// </summary>
    Field,

    /// <summary>
    /// 現在の field token 内 value prefix を置換する候補です。
    /// </summary>
    Value,

    /// <summary>
    /// 検索欄全体を過去の検索文字列に置換する候補です。
    /// </summary>
    History
}

/// <summary>
/// 検索欄 popup に表示する 1 件の補完・履歴候補です。
/// WPF binding から参照するため、表示用 property は public にしています。
/// </summary>
public sealed class KeywordSearchSuggestionItem
{
    /// <summary>
    /// 候補の表示名と置換範囲を指定してインスタンスを作ります。
    /// </summary>
    /// <param name="kind">候補種類。</param>
    /// <param name="displayText">popup に表示する文字列。</param>
    /// <param name="insertionText">検索欄へ挿入する文字列。</param>
    /// <param name="replacementStart">置換開始位置。</param>
    /// <param name="replacementLength">置換文字数。</param>
    internal KeywordSearchSuggestionItem(KeywordSearchSuggestionKind kind, string displayText, string insertionText, int replacementStart, int replacementLength)
    {
        Kind = kind;
        DisplayText = displayText ?? string.Empty;
        InsertionText = insertionText ?? string.Empty;
        ReplacementStart = Math.Max(0, replacementStart);
        ReplacementLength = Math.Max(0, replacementLength);
    }

    /// <summary>
    /// 候補種類です。
    /// </summary>
    internal KeywordSearchSuggestionKind Kind { get; }

    /// <summary>
    /// popup に表示する文字列です。
    /// </summary>
    public string DisplayText { get; }

    /// <summary>
    /// 検索欄へ挿入する文字列です。
    /// </summary>
    internal string InsertionText { get; }

    /// <summary>
    /// 置換開始位置です。
    /// </summary>
    internal int ReplacementStart { get; }

    /// <summary>
    /// 置換文字数です。
    /// </summary>
    internal int ReplacementLength { get; }

    /// <summary>
    /// 候補を検索文字列へ適用します。
    /// </summary>
    /// <param name="sourceText">現在の検索文字列。</param>
    /// <param name="caretIndex">適用後の caret 位置。</param>
    /// <returns>候補適用後の検索文字列。</returns>
    internal string Apply(string sourceText, out int caretIndex)
    {
        string safeText = sourceText ?? string.Empty;
        int replacementStart = Math.Min(ReplacementStart, safeText.Length);
        int replacementLength = Math.Min(ReplacementLength, safeText.Length - replacementStart);
        string appliedText = safeText.Remove(replacementStart, replacementLength).Insert(replacementStart, InsertionText);
        caretIndex = replacementStart + InsertionText.Length;
        return appliedText;
    }
}

/// <summary>
/// field 補完候補の計算結果です。
/// 置換範囲を候補へ焼き込むことで、UI 側は選択された候補をそのまま適用できます。
/// </summary>
internal readonly struct GridKeywordSearchCompletionResult
{
    /// <summary>
    /// 候補一覧を指定して結果を作ります。
    /// </summary>
    /// <param name="items">候補一覧。</param>
    internal GridKeywordSearchCompletionResult(IReadOnlyList<KeywordSearchSuggestionItem> items)
    {
        Items = items ?? [];
    }

    /// <summary>
    /// 空の補完結果です。
    /// </summary>
    internal static GridKeywordSearchCompletionResult Empty { get; } = new GridKeywordSearchCompletionResult([]);

    /// <summary>
    /// 候補一覧です。
    /// </summary>
    internal IReadOnlyList<KeywordSearchSuggestionItem> Items { get; }
}

/// <summary>
/// 検索欄の field 補完を作る helper です。
/// parser と同じ field 定義を使い、field 名そのものだけを補完対象にします。
/// </summary>
internal static class GridKeywordSearchCompletion
{
    /// <summary>
    /// caret 位置の token から field 補完候補を作ります。
    /// </summary>
    /// <param name="keywordFilter">現在の検索文字列。</param>
    /// <param name="caretIndex">現在の caret 位置。</param>
    /// <param name="context">検索欄の文脈。</param>
    /// <returns>field 補完候補。</returns>
    internal static GridKeywordSearchCompletionResult CreateFieldCompletion(string keywordFilter, int caretIndex, GridKeywordSearchContext context)
    {
        string text = keywordFilter ?? string.Empty;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        TokenSpan tokenSpan = FindCurrentToken(text, safeCaretIndex);
        if (tokenSpan.Start >= safeCaretIndex)
        {
            return GridKeywordSearchCompletionResult.Empty;
        }
        string tokenPrefix = text.Substring(tokenSpan.Start, safeCaretIndex - tokenSpan.Start);
        bool isNegated = tokenPrefix.Length > 1 && tokenPrefix[0] == '-';
        int fieldStart = tokenSpan.Start + (isNegated ? 1 : 0);
        string fieldPrefix = text.Substring(fieldStart, safeCaretIndex - fieldStart);
        if (string.IsNullOrWhiteSpace(fieldPrefix) || ContainsCompletionBoundary(fieldPrefix))
        {
            return GridKeywordSearchCompletionResult.Empty;
        }
        string[] candidates = [.. GridKeywordSearchQuery.GetKnownFields(context)
            .Where(field => field.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)];
        if (candidates.Length == 0)
        {
            return GridKeywordSearchCompletionResult.Empty;
        }
        KeywordSearchSuggestionItem[] items = [.. candidates
            .Select(field => new KeywordSearchSuggestionItem(
                KeywordSearchSuggestionKind.Field,
                (isNegated ? "-" : string.Empty) + field + ":",
                field + ":",
                fieldStart,
                safeCaretIndex - fieldStart))];
        return new GridKeywordSearchCompletionResult(items);
    }

    /// <summary>
    /// playlist/ref/table field の値として使う playlist 名補完候補を作ります。
    /// </summary>
    /// <param name="keywordFilter">現在の検索文字列。</param>
    /// <param name="caretIndex">現在の caret 位置。</param>
    /// <param name="context">検索欄の文脈。</param>
    /// <param name="playlistNames">候補に使う playlist 名。</param>
    /// <returns>playlist 名補完候補。</returns>
    internal static GridKeywordSearchCompletionResult CreatePlaylistValueCompletion(string keywordFilter, int caretIndex, GridKeywordSearchContext context, IEnumerable<string> playlistNames)
    {
        if (!TryGetPlaylistValueCompletionContext(keywordFilter, caretIndex, context, out int valueStart, out int replacementLength, out string rawValuePrefix))
        {
            return GridKeywordSearchCompletionResult.Empty;
        }
        string valuePrefix = NormalizeValuePrefix(rawValuePrefix);
        string[] candidates = [.. (playlistNames ?? [])
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => valuePrefix.Length == 0 || name.StartsWith(valuePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        if (candidates.Length == 0)
        {
            return GridKeywordSearchCompletionResult.Empty;
        }
        KeywordSearchSuggestionItem[] items = [.. candidates
            .Select(name => new KeywordSearchSuggestionItem(
                KeywordSearchSuggestionKind.Value,
                name,
                QuoteValueIfNeeded(name),
                valueStart,
                replacementLength))];
        return new GridKeywordSearchCompletionResult(items);
    }

    /// <summary>
    /// 現在の caret 位置が playlist/ref/table value 補完を出せる場所かどうかを返します。
    /// 候補元 playlist を集める前の軽量判定に使います。
    /// </summary>
    /// <param name="keywordFilter">現在の検索文字列。</param>
    /// <param name="caretIndex">現在の caret 位置。</param>
    /// <param name="context">検索欄の文脈。</param>
    /// <returns>playlist/ref/table value 補完対象なら true。</returns>
    internal static bool IsPlaylistValueCompletionContext(string keywordFilter, int caretIndex, GridKeywordSearchContext context)
    {
        return TryGetPlaylistValueCompletionContext(keywordFilter, caretIndex, context, out _, out _, out _);
    }

    private static bool IsPlaylistValueCompletionField(string field)
    {
        return string.Equals(field, "playlist", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field, "ref", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field, "table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetPlaylistValueCompletionContext(string keywordFilter, int caretIndex, GridKeywordSearchContext context, out int valueStart, out int replacementLength, out string rawValuePrefix)
    {
        valueStart = 0;
        replacementLength = 0;
        rawValuePrefix = string.Empty;
        if (context == GridKeywordSearchContext.PlaylistSummary)
        {
            return false;
        }
        string text = keywordFilter ?? string.Empty;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        TokenSpan tokenSpan = FindCurrentToken(text, safeCaretIndex);
        if (tokenSpan.Start >= safeCaretIndex)
        {
            return false;
        }
        string tokenPrefix = text.Substring(tokenSpan.Start, safeCaretIndex - tokenSpan.Start);
        bool isNegated = tokenPrefix.Length > 1 && tokenPrefix[0] == '-';
        int colonIndex = tokenPrefix.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }
        string field = tokenPrefix.Substring(isNegated ? 1 : 0, colonIndex - (isNegated ? 1 : 0));
        if (!IsPlaylistValueCompletionField(field))
        {
            return false;
        }
        valueStart = tokenSpan.Start + colonIndex + 1;
        if (valueStart > safeCaretIndex)
        {
            return false;
        }
        rawValuePrefix = text.Substring(valueStart, safeCaretIndex - valueStart);
        if (rawValuePrefix.IndexOf('|') >= 0)
        {
            return false;
        }
        replacementLength = safeCaretIndex - valueStart;
        return true;
    }

    private static string NormalizeValuePrefix(string rawValuePrefix)
    {
        string value = rawValuePrefix ?? string.Empty;
        if (value.Length == 0 || value[0] != '"')
        {
            return value.Trim();
        }
        var builder = new StringBuilder(value.Length);
        bool escaping = false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (escaping)
            {
                builder.Append(c == '"' || c == '\\' ? c : '\\');
                if (c != '"' && c != '\\')
                {
                    builder.Append(c);
                }
                escaping = false;
                continue;
            }
            if (c == '\\')
            {
                escaping = true;
                continue;
            }
            if (c == '"')
            {
                break;
            }
            builder.Append(c);
        }
        if (escaping)
        {
            builder.Append('\\');
        }
        return builder.ToString();
    }

    private static string QuoteValueIfNeeded(string value)
    {
        string text = value ?? string.Empty;
        bool needsQuote = text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\\' || c == '|');
        if (!needsQuote)
        {
            return text;
        }
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');
        foreach (char c in text)
        {
            if (c == '"' || c == '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static TokenSpan FindCurrentToken(string text, int caretIndex)
    {
        text ??= string.Empty;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        int start = 0;
        bool inQuote = false;
        bool escaping = false;
        for (int i = 0; i < safeCaretIndex; i++)
        {
            char c = text[i];
            if (escaping)
            {
                escaping = false;
                continue;
            }
            if (c == '\\')
            {
                escaping = true;
                continue;
            }
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                start = i + 1;
            }
        }
        int end = safeCaretIndex;
        for (; end < text.Length; end++)
        {
            char c = text[end];
            if (escaping)
            {
                escaping = false;
                continue;
            }
            if (c == '\\')
            {
                escaping = true;
                continue;
            }
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                break;
            }
        }
        return new TokenSpan(start, end);
    }

    private static bool ContainsCompletionBoundary(string value)
    {
        foreach (char c in value ?? string.Empty)
        {
            if (c == ':' || c == '"' || c == '|' || c == '\\')
            {
                return true;
            }
        }
        return false;
    }

    private readonly struct TokenSpan
    {
        internal TokenSpan(int start, int end)
        {
            Start = start;
            End = end;
        }

        internal int Start { get; }

        internal int End { get; }
    }
}
