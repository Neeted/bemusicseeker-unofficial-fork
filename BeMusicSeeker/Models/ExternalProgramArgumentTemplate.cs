using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace BeMusicSeeker.Models;

/// <summary>
/// Windows command-line 相当の argument template を parse 済み token として保持します。
/// </summary>
internal sealed class ExternalProgramArgumentTemplate
{
    /// <summary>
    /// 外部 program argument に許可する唯一の placeholder 名です。
    /// </summary>
    internal const string FilePathPlaceholder = "filePath";

    private readonly IReadOnlyList<string> tokens;

    private ExternalProgramArgumentTemplate(string source, IReadOnlyList<string> tokens)
    {
        Source = source;
        this.tokens = tokens;
    }

    /// <summary>
    /// parse 前の template 文字列を取得します。
    /// </summary>
    internal string Source { get; }

    /// <summary>
    /// placeholder 置換前の token 列を取得します。
    /// </summary>
    internal IReadOnlyList<string> Tokens => tokens;

    /// <summary>
    /// template を parse します。失敗時は caller が設定を無効として診断できる error を返します。
    /// </summary>
    internal static bool TryParse(
        string source,
        out ExternalProgramArgumentTemplate template,
        out string error)
    {
        template = null;
        error = null;
        if (source == null)
        {
            error = "Argument template is required.";
            return false;
        }

        if (!TryValidatePlaceholders(source, out error))
        {
            return false;
        }

        if (!TryTokenize(source, out IReadOnlyList<string> parsedTokens, out error))
        {
            return false;
        }

        if (!ContainsFilePathPlaceholder(source))
        {
            error = "Argument template must contain {filePath}.";
            return false;
        }

        template = new ExternalProgramArgumentTemplate(source, parsedTokens);
        return true;
    }

    /// <summary>
    /// file path を全 token 内の placeholder へ置換し、後続 gateway の ArgumentList に渡せる列を返します。
    /// </summary>
    internal IReadOnlyList<string> Expand(string filePath)
    {
        if (filePath == null)
        {
            throw new ArgumentNullException(nameof(filePath));
        }

        var expanded = new List<string>(tokens.Count);
        foreach (string token in tokens)
        {
            expanded.Add(token.Replace(
                "{" + FilePathPlaceholder + "}",
                filePath,
                StringComparison.Ordinal));
        }

        return new ReadOnlyCollection<string>(expanded);
    }

    private static bool TryValidatePlaceholders(string source, out string error)
    {
        error = null;
        bool foundFilePath = false;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (current == '}')
            {
                error = "Argument template contains an unbalanced placeholder.";
                return false;
            }
            if (current != '{')
            {
                continue;
            }

            int closeIndex = source.IndexOf('}', index + 1);
            if (closeIndex < 0)
            {
                error = "Argument template contains an unbalanced placeholder.";
                return false;
            }

            string placeholder = source.Substring(index + 1, closeIndex - index - 1);
            if (!string.Equals(placeholder, FilePathPlaceholder, StringComparison.Ordinal))
            {
                error = "Argument template contains an unknown placeholder: " + placeholder;
                return false;
            }

            foundFilePath = true;
            index = closeIndex;
        }

        if (!foundFilePath)
        {
            error = "Argument template must contain {filePath}.";
            return false;
        }

        return true;
    }

    private static bool ContainsFilePathPlaceholder(string source)
    {
        return source.Contains("{" + FilePathPlaceholder + "}", StringComparison.Ordinal);
    }

    private static bool TryTokenize(
        string source,
        out IReadOnlyList<string> tokens,
        out string error)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        bool insideQuotes = false;
        bool tokenStarted = false;

        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (current == '\\')
            {
                int slashStart = index;
                while (index < source.Length && source[index] == '\\')
                {
                    index++;
                }
                int slashCount = index - slashStart;
                bool followedByQuote = index < source.Length && source[index] == '"';
                if (!followedByQuote)
                {
                    token.Append('\\', slashCount);
                    tokenStarted = true;
                    index--;
                    continue;
                }

                token.Append('\\', slashCount / 2);
                tokenStarted = true;
                if ((slashCount & 1) != 0)
                {
                    token.Append('"');
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }
                continue;
            }

            if (current == '"')
            {
                insideQuotes = !insideQuotes;
                tokenStarted = true;
                continue;
            }

            if ((current == ' ' || current == '\t') && !insideQuotes)
            {
                if (tokenStarted)
                {
                    result.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }
                continue;
            }

            token.Append(current);
            tokenStarted = true;
        }

        if (insideQuotes)
        {
            tokens = [];
            error = "Argument template contains an unbalanced double quote.";
            return false;
        }

        if (tokenStarted)
        {
            result.Add(token.ToString());
        }

        tokens = new ReadOnlyCollection<string>(result);
        error = null;
        return true;
    }
}
