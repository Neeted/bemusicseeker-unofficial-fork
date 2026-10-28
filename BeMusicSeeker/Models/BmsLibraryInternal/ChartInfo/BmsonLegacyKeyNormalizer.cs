using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>数値字句・重複プロパティ・オブジェクト順を保持して、旧bmsonのキーだけをDTOの入力名へ揃えます。</summary>
internal sealed class BmsonLegacyKeyNormalizer
{
    private readonly string json;
    private readonly JsonTextReader reader;
    private readonly List<int> lineStarts = [0];
    private readonly List<(int Start, int End, string Text)> edits = [];
    private readonly List<(int Start, int End, string Text)> resolutions = [];
    private int end;
    private int start;
    private bool hasVersion;
    private string version;
    private bool hasLegacyRootKey;
    private bool lastInfoHasLegacyKey;

    private BmsonLegacyKeyNormalizer(string json, JsonTextReader reader)
    {
        this.json = json;
        this.reader = reader;
        for (int index = 0; index < json.Length; index++)
        {
            if (json[index] == '\r')
            {
                if (index + 1 < json.Length && json[index + 1] == '\n') { index++; }
                lineStarts.Add(index + 1);
            }
            else if (json[index] == '\n') { lineStarts.Add(index + 1); }
        }
    }

    /// <summary>原文構文を先に読み、各原文オブジェクトの現代名を優先します。旧形式のresolutionはDTO変換前に240へ固定します。</summary>
    internal static string Normalize(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var normalizer = new BmsonLegacyKeyNormalizer(json, reader);
        if (normalizer.Read() && reader.TokenType == JsonToken.StartObject)
        {
            normalizer.ReadObject("root");
        }
        if (normalizer.version == "0.21" || (!normalizer.hasVersion && (normalizer.hasLegacyRootKey || normalizer.lastInfoHasLegacyKey)))
        {
            normalizer.edits.AddRange(normalizer.resolutions);
        }
        if (normalizer.edits.Count == 0) { return json; }
        var result = new StringBuilder(json.Length);
        int position = 0;
        foreach ((int Start, int End, string Text) edit in normalizer.edits.OrderBy(edit => edit.Start))
        {
            result.Append(json, position, edit.Start - position);
            result.Append(edit.Text);
            position = edit.End;
        }
        result.Append(json, position, json.Length - position);
        return result.ToString();
    }

    private void ReadObject(string kind)
    {
        var properties = new List<(string Name, int Start, int End)>();
        while (Read() && reader.TokenType == JsonToken.PropertyName)
        {
            string name = (string)reader.Value;
            properties.Add((name, start, end));
            if (!Read()) { throw new JsonSerializationException("Unexpected end of bmson property."); }
            int valueStart = start;
            string childKind = kind == "root" ? CanonicalRootName(name) : string.Empty;
            if (kind == "root" && string.Equals(name, "version", StringComparison.OrdinalIgnoreCase))
            {
                hasVersion = true;
                version = Convert.ToString(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (kind == "root" && name is "soundChannel" or "bpmNotes" or "stopNotes") { hasLegacyRootKey = true; }
            if (childKind == "info") { lastInfoHasLegacyKey = false; }
            if (reader.TokenType == JsonToken.StartObject && childKind == "info")
            {
                ReadObject("info");
            }
            else if (reader.TokenType == JsonToken.StartArray && childKind is "bpm_events" or "stop_events")
            {
                ReadEvents(childKind);
            }
            else
            {
                reader.Skip();
                end = Position();
            }
            if (kind == "info" && string.Equals(name, "resolution", StringComparison.OrdinalIgnoreCase))
            {
                resolutions.Add((valueStart, end, "240"));
            }
        }
        // DTOのDeserializeと同じく、構文の途中で尽きた入力はserialization失敗として返します。
        if (reader.TokenType != JsonToken.EndObject) { throw new JsonSerializationException("Unexpected end of bmson object."); }
        if (kind == "root")
        {
            Alias("bpm_events", "bpmNotes");
            Alias("stop_events", "stopNotes");
            Alias("sound_channels", "soundChannel");
        }
        else if (kind == "info")
        {
            // 形式の判定は再生adapterと同じ最後のinfoを使い、DTOのpopulateに使う各objectは集約しません。
            lastInfoHasLegacyKey = properties.Any(property => property.Name == "initBPM");
            Alias("init_bpm", "initBPM");
        }
        else if (kind == "bpm_events") { Alias("bpm", "v"); }
        else if (kind == "stop_events") { Alias("duration", "v"); }

        void Alias(string modern, string legacy)
        {
            // DTOの大小文字を区別しない照合も存在として扱い、現代名の原文綴りは変更しません。
            if (properties.Any(property => string.Equals(property.Name, modern, StringComparison.OrdinalIgnoreCase))) { return; }
            foreach ((string Name, int Start, int End) property in properties.Where(property => property.Name == legacy))
            {
                edits.Add((property.Start, property.End, "\"" + modern + "\":"));
            }
        }
    }

    private void ReadEvents(string kind)
    {
        while (Read() && reader.TokenType != JsonToken.EndArray)
        {
            if (reader.TokenType == JsonToken.StartObject) { ReadObject(kind); }
            else { reader.Skip(); end = Position(); }
        }
        if (reader.TokenType != JsonToken.EndArray) { throw new JsonSerializationException("Unexpected end of bmson array."); }
    }

    private static string CanonicalRootName(string name)
    {
        if (string.Equals(name, "info", StringComparison.OrdinalIgnoreCase)) { return "info"; }
        if (name == "bpmNotes" || string.Equals(name, "bpm_events", StringComparison.OrdinalIgnoreCase)) { return "bpm_events"; }
        if (name == "stopNotes" || string.Equals(name, "stop_events", StringComparison.OrdinalIgnoreCase)) { return "stop_events"; }
        return string.Empty;
    }

    private bool Read()
    {
        int previousEnd = end;
        while (reader.Read())
        {
            end = Position();
            if (reader.TokenType == JsonToken.Comment) { previousEnd = end; continue; }
            start = previousEnd;
            while (start < json.Length && (char.IsWhiteSpace(json[start]) || json[start] is ',' or ':')) { start++; }
            return true;
        }
        return false;
    }

    private int Position() => lineStarts[reader.LineNumber - 1] + reader.LinePosition;
}
