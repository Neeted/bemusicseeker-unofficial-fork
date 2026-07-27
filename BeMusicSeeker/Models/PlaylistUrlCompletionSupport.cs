using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

/// <summary>
/// プレイリスト URL 補完で使う入力解析と URI 正規化をまとめます。
/// <see cref="BMSPlaylist"/> 本体から分離し、単体テストしやすい純粋ロジックとして保持します。
/// </summary>
internal static class PlaylistUrlCompletionSupport
{
    /// <summary>
    /// MD5 と URL の対応表を取得する既定の TSV URI です。
    /// 設定未指定時でも補完機能が最低限動くよう、既定配布先をここに集約します。
    /// </summary>
    internal const string DefaultMd5UrlMappingTsvUri = "https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/bms-md5-url-map.tsv";

    /// <summary>
    /// Stella Uploader の URL1/URL2 付き送信一覧 JSON 取得先です。
    /// </summary>
    internal const string StellaScoreUploadFullJsonUri = "https://stellabms.xyz/score_upload_full.json";

    /// <summary>
    /// 補完元 URI またはローカルファイルパスを解決します。
    /// http/https/file と、Windows の絶対パス表記を受け入れます。
    /// </summary>
    /// <param name="rawValue">設定に保存された生文字列。</param>
    /// <param name="uri">解決できた絶対 URI。</param>
    /// <returns>利用可能な URI として解決できた場合は true。</returns>
    internal static bool TryResolveSourceUri(string rawValue, out Uri uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }
        string normalizedValue = rawValue.Trim();
        if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out Uri resolvedUri))
        {
            if (!Path.IsPathRooted(normalizedValue))
            {
                return false;
            }
            try
            {
                resolvedUri = new Uri(Path.GetFullPath(normalizedValue));
            }
            catch
            {
                return false;
            }
        }
        if (!resolvedUri.IsAbsoluteUri)
        {
            return false;
        }
        if (!string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !string.Equals(resolvedUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        uri = resolvedUri;
        return true;
    }

    /// <summary>
    /// MD5-URL 対応 TSV を 1 回分のスナップショットへ変換します。
    /// 同一 MD5 は先勝ちにし、不正 MD5 や絶対 URI にならない列は無視します。
    /// </summary>
    /// <param name="content">TSV の全文字列。</param>
    /// <returns>適用用のスナップショット。</returns>
    internal static PlaylistUrlCompletionSourceSnapshot ParseMd5UrlMappingTsv(string content)
    {
        var candidates = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);
        int duplicateCount = 0;
        int ignoredRowCount = 0;
        using var reader = new StringReader(content ?? string.Empty);
        string line = reader.ReadLine();
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                ignoredRowCount++;
                continue;
            }
            string[] columns = line.Split('\t');
            if (columns.Length == 0)
            {
                ignoredRowCount++;
                continue;
            }
            string normalizedMd5 = NormalizeMd5(columns[0]);
            if (normalizedMd5 == null)
            {
                ignoredRowCount++;
                continue;
            }
            Uri urlDiff = TryParseAbsoluteUri(columns.Length > 1 ? columns[1] : null);
            Uri url = TryParseAbsoluteUri(columns.Length > 2 ? columns[2] : null);
            if (url == null && urlDiff == null)
            {
                ignoredRowCount++;
                continue;
            }
            if (candidates.ContainsKey(normalizedMd5))
            {
                duplicateCount++;
                continue;
            }
            candidates.Add(normalizedMd5, new PlaylistUrlCompletionCandidate(url, urlDiff));
        }
        return new PlaylistUrlCompletionSourceSnapshot(candidates, duplicateCount, ignoredRowCount);
    }

    /// <summary>
    /// Stella Uploader の score_upload_full.json を URL1/URL2 補完用スナップショットへ変換します。
    /// 同一 MD5 は先勝ちにし、不正 MD5 や絶対 URI にならない列は無視します。
    /// </summary>
    /// <param name="content">JSON の全文字列。</param>
    /// <returns>URL1/URL2 補完用のスナップショット。</returns>
    internal static PlaylistUrlCompletionSourceSnapshot ParseStellaUploadFullJson(string content)
    {
        var candidates = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);
        int duplicateCount = 0;
        int ignoredRowCount = 0;
        JArray rows = ParseJsonArray(content ?? "[]");
        foreach (JToken row in rows)
        {
            string normalizedMd5 = NormalizeMd5(GetJsonString(row, "md5"));
            if (normalizedMd5 == null)
            {
                ignoredRowCount++;
                continue;
            }
            Uri url = TryParseAbsoluteUri(GetJsonString(row, "url"));
            Uri urlDiff = TryParseAbsoluteUri(GetJsonString(row, "url_diff"));
            if (url == null && urlDiff == null)
            {
                ignoredRowCount++;
                continue;
            }
            if (candidates.ContainsKey(normalizedMd5))
            {
                duplicateCount++;
                continue;
            }
            candidates.Add(normalizedMd5, new PlaylistUrlCompletionCandidate(url, urlDiff));
        }
        return new PlaylistUrlCompletionSourceSnapshot(candidates, duplicateCount, ignoredRowCount);
    }

    /// <summary>
    /// 補完対象として扱える MD5 を小文字へ正規化します。
    /// </summary>
    /// <param name="value">判定対象の文字列。</param>
    /// <returns>正規化済み MD5。無効なら null。</returns>
    internal static string NormalizeMd5(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string trimmedValue = value.Trim();
        if (!LR2SongDB.md5HashRegex.IsMatch(trimmedValue))
        {
            return null;
        }
        return trimmedValue.ToLowerInvariant();
    }

    private static string GetJsonString(JToken row, string memberName)
    {
        if (row is not JObject jsonObject || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }
        if (!jsonObject.TryGetValue(memberName, out JToken value) || value.Type == JTokenType.Null)
        {
            return null;
        }
        return value.ToString();
    }

    private static JArray ParseJsonArray(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None
        };
        JToken token = JToken.ReadFrom(reader);
        if (reader.Read())
        {
            throw new JsonReaderException("JSON document contains trailing content.");
        }
        return token as JArray ?? throw new FormatException("JSON document must be an array.");
    }

    private static Uri TryParseAbsoluteUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri resolvedUri) || !resolvedUri.IsAbsoluteUri)
        {
            return null;
        }
        return resolvedUri;
    }
}

/// <summary>
/// 1 つの MD5 に対して補完できた URL 候補群を保持します。
/// TSV と Stella の両方で同じ形式を使い、最終適用時だけ優先順位を決めます。
/// </summary>
internal sealed class PlaylistUrlCompletionCandidate
{
    /// <summary>
    /// 補完候補の URL1 です。
    /// </summary>
    internal Uri Url { get; }

    /// <summary>
    /// 補完候補の URL2 です。
    /// </summary>
    internal Uri UrlDiff { get; }

    /// <summary>
    /// 候補が少なくとも 1 つ存在するかどうかを返します。
    /// </summary>
    internal bool HasAnyValue => Url != null || UrlDiff != null;

    /// <summary>
    /// 候補を初期化します。
    /// </summary>
    /// <param name="url">URL1 候補。</param>
    /// <param name="urlDiff">URL2 候補。</param>
    internal PlaylistUrlCompletionCandidate(Uri url, Uri urlDiff)
    {
        Url = url;
        UrlDiff = urlDiff;
    }
}

/// <summary>
/// 1 系統分の補完元を解析した結果を保持します。
/// 適用件数だけでなく duplicate / ignore の統計もログとテストで利用します。
/// </summary>
internal sealed class PlaylistUrlCompletionSourceSnapshot
{
    /// <summary>
    /// 空のスナップショットです。
    /// </summary>
    internal static PlaylistUrlCompletionSourceSnapshot Empty { get; } = new PlaylistUrlCompletionSourceSnapshot(new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase), 0, 0);

    /// <summary>
    /// MD5 ごとの補完候補一覧です。
    /// </summary>
    internal IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> Candidates { get; }

    /// <summary>
    /// 先勝ちルールで読み飛ばした重複件数です。
    /// </summary>
    internal int DuplicateCount { get; }

    /// <summary>
    /// 不正形式や空欄などで無視した件数です。
    /// </summary>
    internal int IgnoredRowCount { get; }

    /// <summary>
    /// 有効候補として採用できた件数です。
    /// </summary>
    internal int CandidateCount => Candidates.Count;

    /// <summary>
    /// スナップショットを初期化します。
    /// </summary>
    /// <param name="candidates">採用済み候補一覧。</param>
    /// <param name="duplicateCount">重複スキップ件数。</param>
    /// <param name="ignoredRowCount">無視件数。</param>
    internal PlaylistUrlCompletionSourceSnapshot(IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> candidates, int duplicateCount, int ignoredRowCount)
    {
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        DuplicateCount = duplicateCount;
        IgnoredRowCount = ignoredRowCount;
    }
}
