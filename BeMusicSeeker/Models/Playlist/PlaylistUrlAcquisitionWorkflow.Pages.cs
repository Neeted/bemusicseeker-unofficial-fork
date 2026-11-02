using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sgml;

namespace BeMusicSeeker.Models;

internal sealed partial class PlaylistUrlAcquisitionWorkflow
{
    private static bool Tag(XElement element, string name) => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static XDocument ParseDownloadPage(string text)
    {
        // IgnoreDtdはHTML内蔵文法の遅延読込みも止めるため、入力を渡す前に内蔵文法だけを明示取得します。
        // 取得後の外部DOCTYPE参照は禁止したまま、断片の共通rootと省略終了タグを復元します。
        using var grammarReader = new SgmlReader { DocType = "HTML" };
        using var reader = new SgmlReader { DocType = "HTML", Dtd = grammarReader.Dtd, InputStream = new StringReader(text), IgnoreDtd = true };
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    // 取得済み本文だけを解析し、候補選択とスキーム拒否を同じ有限集合へ限定します。
    private static (Uri resolvedUri, bool unsupportedScheme) ResolveSharedDownloadPage(Uri pageUri, string text)
    {
        if (pageUri == null || string.IsNullOrWhiteSpace(text)) { return (null, false); }
        XDocument document;
        try { document = ParseDownloadPage(text); }
        catch (XmlException) { return (null, false); }
        if (IsGoogleDriveHost(pageUri)) { return ResolveDriveForm(pageUri, document); }
        IEnumerable<string> candidates;
        bool mediaFire = IsExactHostOrSubdomain(pageUri.Host, "mediafire.com");
        if (mediaFire)
        {
            candidates = document.Descendants().Where(element => Tag(element, "a")
                && (string.Equals(Attribute(element, "id"), "downloadButton", StringComparison.OrdinalIgnoreCase)
                    || Attribute(element, "class")?.Contains("popsok", StringComparison.OrdinalIgnoreCase) == true))
                .Select(element => Attribute(element, "href"));
        }
        else if (IsManbowDownloadPageUri(pageUri)) { candidates = ManbowCandidates(document); }
        else if (IsVenueBmsSearchUri(pageUri) || IsBmsSearchInfoUri(pageUri)) { candidates = StructuredDownloadCandidates(pageUri, document); }
        else { return (null, false); }
        bool nonHttp = false, http = false;
        foreach (string textUri in candidates)
        {
            if (string.IsNullOrWhiteSpace(textUri) || !Uri.TryCreate(pageUri, textUri.Trim(), out Uri candidate)) { continue; }
            if (!IsHttpOrHttps(candidate)) { nonHttp = true; continue; }
            http = true;
            if (mediaFire)
            {
                if (IsExactHostOrSubdomain(candidate.Host, "mediafire.com")) { return (candidate, false); }
            }
            else if (TryCreateResolvableDownloadUri(pageUri, textUri, IsManbowDownloadPageUri(pageUri), out Uri resolved))
            { return (resolved, false); }
        }
        return (null, nonHttp && !http);
    }

    private static (Uri resolvedUri, bool unsupportedScheme) ResolveDriveForm(Uri pageUri, XDocument document)
    {
        XElement form = document.Descendants().FirstOrDefault(element => Tag(element, "form")
            && string.Equals(Attribute(element, "id"), "download-form", StringComparison.OrdinalIgnoreCase));
        if (form == null || Attribute(form, "method") is string method && !method.Equals("get", StringComparison.OrdinalIgnoreCase)) { return (null, false); }
        if (!Uri.TryCreate(pageUri, Attribute(form, "action") ?? pageUri.ToString(), out Uri action)) { return (null, false); }
        if (!IsHttpOrHttps(action)) { return (null, true); }
        if (!IsGoogleDriveHost(action)) { return (null, false); }
        List<KeyValuePair<string, string>> query = ParseQueryParameters(action.Query);
        foreach (XElement input in form.Descendants().Where(element => Tag(element, "input")))
        {
            string name = Attribute(input, "name"), type = Attribute(input, "type");
            if (string.IsNullOrWhiteSpace(name) || Attribute(input, "disabled") != null
                || string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "button", StringComparison.OrdinalIgnoreCase)) { continue; }
            SetQueryParameter(query, name, Attribute(input, "value") ?? string.Empty);
        }
        return (BuildUriWithQuery(action, query), false);
    }

    private static IEnumerable<string> ManbowCandidates(XDocument document)
    {
        XElement heading = document.Descendants().FirstOrDefault(element => (Tag(element, "th") || Tag(element, "td"))
            && string.Concat(element.Value.Where(character => !char.IsWhiteSpace(character))).Equals("DownloadAddress", StringComparison.OrdinalIgnoreCase));
        XElement cell = heading?.ElementsAfterSelf().FirstOrDefault();
        if (cell == null || !Tag(cell, "td")) { yield break; }
        bool first = true;
        foreach (XElement anchor in cell.Descendants().Where(element => Tag(element, "a")))
        {
            string context = anchor.Value;
            // リンク直前から同じ行までの説明だけを結び付け、次行・別項目へ説明を広げません。
            foreach (XNode node in anchor.NodesBeforeSelf().Reverse())
            {
                if (node is XElement previous && (Tag(previous, "br") || Tag(previous, "a"))) { break; }
                context = (node is XText value ? value.Value : (node as XElement)?.Value) + context;
            }
            bool mirror = context.Contains("mirror", StringComparison.OrdinalIgnoreCase) || context.Contains("ミラー", StringComparison.Ordinal);
            bool additional = Regex.IsMatch(context, "BGA|追加|差分|高画質|高解像|additional", RegexOptions.IgnoreCase);
            if (first || mirror && !additional) { yield return Attribute(anchor, "href"); }
            first = false;
        }
    }

    private static IEnumerable<JObject> JsonObjects(JToken token)
        => token is JObject value ? value.DescendantsAndSelf().OfType<JObject>() : token is JContainer container ? container.Descendants().OfType<JObject>() : [];

    // 既知Next文字列を文書順に復号・連結し、通常JSONレコードだけを読みます。
    private static IReadOnlyList<JToken> ReadEmbeddedDocuments(XDocument document)
    {
        var result = new List<JToken>();
        string rootText = document.Root?.Value.Trim() ?? string.Empty;
        if (rootText.StartsWith('{') || rootText.StartsWith('['))
        { try { result.Add(JToken.Parse(rootText)); } catch (JsonException) { } }
        var flight = new StringBuilder();
        foreach (XElement script in document.Descendants().Where(element => Tag(element, "script")))
        {
            string text = script.Value.Trim();
            if (text.StartsWith('{') || text.StartsWith('['))
            {
                try { result.Add(JToken.Parse(text)); } catch (JsonException) { }
            }
            int offset = 0;
            const string marker = "self.__next_f.push(";
            while ((offset = text.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
            {
                offset += marker.Length;
                using var jsonReader = new JsonTextReader(new StringReader(text[offset..]));
                try
                {
                    var chunk = JToken.ReadFrom(jsonReader);
                    if (chunk is JArray array && array.Count >= 2 && array[0].Type == JTokenType.Integer
                        && (int)array[0] == 1 && array[1].Type == JTokenType.String) { flight.Append((string)array[1]); }
                }
                catch (JsonException) { }
            }
        }
        foreach (string line in flight.ToString().Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 1 || !line[..separator].All(Uri.IsHexDigit)) { continue; }
            string json = line[(separator + 1)..].TrimStart();
            if (!json.StartsWith('{') && !json.StartsWith('[')) { continue; }
            try { result.Add(JToken.Parse(json)); } catch (JsonException) { }
        }
        return result;
    }

    private static IEnumerable<string> StructuredDownloadCandidates(Uri pageUri, XDocument document)
    {
        var objects = ReadEmbeddedDocuments(document).SelectMany(JsonObjects).ToList();
        string[] segments = pageUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool venue = IsVenueBmsSearchUri(pageUri);
        var selected = new List<string>();
        bool identified = false;
        if (venue)
        {
            foreach (JObject item in objects)
            {
                if (item["downloadLinks"] is not JArray links || item["exhibition"] is not JObject exhibition
                    || (string)exhibition["alias"] != segments[^2] || item["number"]?.ToString() != segments[^1]) { continue; }
                identified = true;
                selected.AddRange(links.OfType<JObject>().Where(link => string.Equals((string)link["type"], "CORE", StringComparison.OrdinalIgnoreCase))
                    .Select(link => (string)link["downloadURL"]).Where(url => url != null));
            }
        }
        else
        {
            foreach (JObject item in objects)
            {
                if (item["id"]?.ToString() != segments[^1] || item["downloads"] is not JArray downloads) { continue; }
                // 同IDの省略形は配布候補を持つ完全データの代わりにしません。
                if (!downloads.OfType<JObject>().Any(link => link["url"]?.Type == JTokenType.String)) { continue; }
                identified = true;
                selected.AddRange(downloads.OfType<JObject>().Select(link => (string)link["url"]).Where(url => url != null));
            }
        }
        if (identified) { return selected; }
        // 識別情報を持たない旧埋込み入力は既存契約として残します。別作品の構造化データはここへ降ろしません。
        bool hasIdentifiedWorks = objects.Any(item => venue ? item["number"] != null && item["exhibition"] is JObject
            : item["id"] != null && item["downloads"] is JArray);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (JObject item in objects)
        {
            if (venue && item["downloadURL"]?.Type == JTokenType.String)
            {
                string url = (string)item["downloadURL"];
                bool core = string.Equals((string)item["type"], "CORE", StringComparison.OrdinalIgnoreCase);
                if (core && !hasIdentifiedWorks) { selected.Add(url); }
                else if (TryCreateLegacyAnchorCandidate(pageUri, url, out Uri resolved)) { excluded.Add(CreatePlaylistUrlDownloadKey(resolved)); }
            }
            if (!venue && !hasIdentifiedWorks && item["downloads"] is JArray downloads)
            { selected.AddRange(downloads.OfType<JObject>().Select(link => (string)link["url"]).Where(url => url != null)); }
        }
        if (selected.Count > 0) { return selected; }
        return document.Descendants().Where(element => Tag(element, "a")).Select(element => Attribute(element, "href"))
            .Where(url => TryCreateLegacyAnchorCandidate(pageUri, url, out Uri resolved) && !excluded.Contains(CreatePlaylistUrlDownloadKey(resolved)));
    }

    private static bool TryCreateLegacyAnchorCandidate(Uri pageUri, string href, out Uri candidate)
    {
        if (TryCreateResolvableDownloadUri(pageUri, href, false, out candidate)) { return true; }
        // 旧anchor互換でも配布ファイルという形は要求します。作者連絡・画面操作リンクを候補へ広げません。
        if (!Uri.TryCreate(pageUri, href?.Trim(), out candidate) || IsHttpOrHttps(candidate)
            || candidate.Scheme is "mailto" or "javascript") { return false; }
        return IsDownloadAndInstallCandidateFileName(Path.GetFileName(candidate.AbsolutePath));
    }

    private static string DecodeDownloadPage(byte[] bytes, string charset)
    {
        // StreamReaderのBOM検出はHTTP charsetより優先します。
        bool bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) || bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })
            || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff });
        Encoding encoding = TryPageEncoding(charset);
        if (!bom && encoding == null)
        {
            string prefix = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            try
            {
                XDocument document = ParseDownloadPage(prefix);
                foreach (XElement meta in document.Descendants().Where(element => Tag(element, "meta")))
                {
                    string declaration = Attribute(meta, "charset");
                    if (declaration == null && string.Equals(Attribute(meta, "http-equiv"), "content-type", StringComparison.OrdinalIgnoreCase))
                    { declaration = Regex.Match(Attribute(meta, "content") ?? string.Empty, @"charset\s*=\s*([^;\s]+)", RegexOptions.IgnoreCase).Groups[1].Value; }
                    encoding = TryPageEncoding(declaration);
                    if (encoding != null) { break; }
                }
            }
            catch (XmlException) { }
        }
        using var reader = new StreamReader(new MemoryStream(bytes), encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Encoding TryPageEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        try { return Encoding.GetEncoding(name.Trim().Trim('\"', '\'')); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }
}
