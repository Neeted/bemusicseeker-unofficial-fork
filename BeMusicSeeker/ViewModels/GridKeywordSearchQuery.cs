using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class GridKeywordSearchQuery
{
    private readonly SearchToken[] tokens;

    private GridKeywordSearchQuery(SearchToken[] tokens)
    {
        this.tokens = tokens ?? Array.Empty<SearchToken>();
    }

    internal bool HasTokens => tokens.Length > 0;

    internal static GridKeywordSearchQuery Parse(string keywordFilter)
    {
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return new GridKeywordSearchQuery(Array.Empty<SearchToken>());
        }
        SearchToken[] parsedTokens = keywordFilter
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseToken)
            .OrderBy((SearchToken token) => token.Term?.Length ?? 0)
            .ToArray();
        return new GridKeywordSearchQuery(parsedTokens);
    }

    internal bool MatchesBmsFile(BMSFile file)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (file == null)
        {
            return false;
        }
        foreach (SearchToken token in tokens)
        {
            if (!MatchesToken(token, file))
            {
                return false;
            }
        }
        return true;
    }

    internal bool MatchesPlaylistDetail(PlaylistDetailSourceRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchToken token in tokens)
        {
            if (!MatchesToken(token, row))
            {
                return false;
            }
        }
        return true;
    }

    internal bool MatchesPlaylistSummary(PlaylistSummaryRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchToken token in tokens)
        {
            if (!MatchesToken(token, row))
            {
                return false;
            }
        }
        return true;
    }

    private static SearchToken ParseToken(string rawToken)
    {
        string token = rawToken ?? string.Empty;
        int colonIndex = token.IndexOf(':');
        if (colonIndex <= 0)
        {
            return new SearchToken(null, token, isFieldToken: false);
        }
        string field = token.Substring(0, colonIndex).Trim().ToLowerInvariant();
        string term = token.Substring(colonIndex + 1);
        return new SearchToken(field, term, isFieldToken: true);
    }

    private static bool MatchesToken(SearchToken token, BMSFile file)
    {
        if (token.IsInvalid)
        {
            return false;
        }
        if (!token.IsFieldToken)
        {
            return ContainsAnyBmsFileGlobal(file, token.Term);
        }
        switch (token.Field)
        {
            case "title":
                return Contains(file.Title, token.Term);
            case "artist":
                return Contains(file.Artist, token.Term);
            case "genre":
                return Contains(file.genre, token.Term);
            case "tag":
                return Contains(file.tag, token.Term);
            case "path":
                return Contains(file.path, token.Term);
            case "playlist":
            case "ref":
                return Contains(file.RefTablesSymbols, token.Term);
            case "md5":
            case "hash":
                return Contains(file.hash, token.Term);
            case "sha256":
                return Contains(file.sha256, token.Term);
            default:
                return false;
        }
    }

    private static bool MatchesToken(SearchToken token, PlaylistDetailSourceRow row)
    {
        if (token.IsInvalid)
        {
            return false;
        }
        if (!token.IsFieldToken)
        {
            return ContainsAnyPlaylistDetailGlobal(row, token.Term);
        }
        switch (token.Field)
        {
            case "title":
                return Contains(row.Title, token.Term);
            case "artist":
                return Contains(row.Artist, token.Term);
            case "genre":
                return Contains(row.genre, token.Term);
            case "tag":
                return Contains(row.tag, token.Term);
            case "path":
                return Contains(row.path, token.Term);
            case "playlist":
            case "ref":
                return Contains(row.RefTablesSymbols, token.Term);
            case "md5":
            case "hash":
                return Contains(row.hash, token.Term);
            case "sha256":
                return Contains(row.sha256, token.Term);
            case "memo":
                return Contains(row.memo, token.Term);
            case "comment":
                return Contains(row.comment, token.Term);
            default:
                return false;
        }
    }

    private static bool MatchesToken(SearchToken token, PlaylistSummaryRow row)
    {
        if (token.IsInvalid)
        {
            return false;
        }
        string playlistId = row.PlaylistId?.ToString() ?? string.Empty;
        if (!token.IsFieldToken)
        {
            return Contains(playlistId, token.Term)
                || Contains(row.Name, token.Term)
                || Contains(row.Symbol, token.Term);
        }
        switch (token.Field)
        {
            case "id":
                return Contains(playlistId, token.Term);
            case "name":
                return Contains(row.Name, token.Term);
            case "symbol":
                return Contains(row.Symbol, token.Term);
            default:
                return false;
        }
    }

    private static bool ContainsAnyBmsFileGlobal(BMSFile file, string term)
    {
        return Contains(file.Title, term)
            || Contains(file.genre, term)
            || Contains(file.Artist, term)
            || Contains(file.tag, term)
            || Contains(file.path, term)
            || Contains(file.RefTablesSymbols, term)
            || Contains(file.hash, term)
            || Contains(file.sha256, term);
    }

    private static bool ContainsAnyPlaylistDetailGlobal(PlaylistDetailSourceRow row, string term)
    {
        return Contains(row.Title, term)
            || Contains(row.genre, term)
            || Contains(row.Artist, term)
            || Contains(row.tag, term)
            || Contains(row.path, term)
            || Contains(row.RefTablesSymbols, term)
            || Contains(row.hash, term)
            || Contains(row.sha256, term)
            || Contains(row.memo, term)
            || Contains(row.comment, term);
    }

    private static bool Contains(string value, string term)
    {
        return !string.IsNullOrEmpty(term)
            && !string.IsNullOrEmpty(value)
            && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private readonly struct SearchToken
    {
        internal SearchToken(string field, string term, bool isFieldToken)
        {
            Field = field;
            Term = term ?? string.Empty;
            IsFieldToken = isFieldToken;
        }

        internal string Field { get; }

        internal string Term { get; }

        internal bool IsFieldToken { get; }

        internal bool IsInvalid => IsFieldToken && (string.IsNullOrWhiteSpace(Field) || string.IsNullOrEmpty(Term));
    }
}
