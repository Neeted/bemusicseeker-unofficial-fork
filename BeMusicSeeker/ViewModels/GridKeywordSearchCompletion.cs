using System;
using System.Collections.Generic;
using System.Linq;

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
        Items = items ?? Array.Empty<KeywordSearchSuggestionItem>();
    }

    /// <summary>
    /// 空の補完結果です。
    /// </summary>
    internal static GridKeywordSearchCompletionResult Empty { get; } = new GridKeywordSearchCompletionResult(Array.Empty<KeywordSearchSuggestionItem>());

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
        string[] candidates = GridKeywordSearchQuery.GetKnownFields(context)
            .Where((string field) => field.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy((string field) => field, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return GridKeywordSearchCompletionResult.Empty;
        }
        KeywordSearchSuggestionItem[] items = candidates
            .Select((string field) => new KeywordSearchSuggestionItem(
                KeywordSearchSuggestionKind.Field,
                (isNegated ? "-" : string.Empty) + field + ":",
                field + ":",
                fieldStart,
                safeCaretIndex - fieldStart))
            .ToArray();
        return new GridKeywordSearchCompletionResult(items);
    }

    private static TokenSpan FindCurrentToken(string text, int caretIndex)
    {
        int start = caretIndex;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }
        int end = caretIndex;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
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
