using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class BmsonJsonParser
{
    public static BmsonDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BmsonDocument();
        }

        JsonSerializerSettings settings = new JsonSerializerSettings
        {
            Converters = { new BmsonIntJsonConverter(), new BmsonDoubleJsonConverter() },
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
        string normalizedJson = (json ?? string.Empty).TrimStart('\uFEFF');
        BmsonDocument document = JsonConvert.DeserializeObject<BmsonDocument>(normalizedJson, settings) ?? new BmsonDocument();
        ApplyRawNumberTexts(document, normalizedJson);
        return Normalize(document);
    }

    private static BmsonDocument Normalize(BmsonDocument document)
    {
        document.Info ??= new BmsonInfo();
        document.Lines ??= Array.Empty<BmsonBarLine>();
        document.BpmEvents ??= Array.Empty<BmsonBpmEvent>();
        document.StopEvents ??= Array.Empty<BmsonStopEvent>();
        document.ScrollEvents ??= Array.Empty<BmsonScrollEvent>();
        document.SoundChannels ??= Array.Empty<BmsonSoundChannel>();
        document.KeyChannels ??= Array.Empty<BmsonMineChannel>();
        document.MineChannels ??= Array.Empty<BmsonMineChannel>();
        document.Bga ??= new BmsonBga();

        document.Info.Normalize();
        document.Bga.Normalize();
        foreach (BmsonSoundChannel channel in document.SoundChannels)
        {
            channel?.Normalize();
        }
        foreach (BmsonMineChannel channel in document.KeyChannels)
        {
            channel?.Normalize();
        }
        foreach (BmsonMineChannel channel in document.MineChannels)
        {
            channel?.Normalize();
        }
        return document;
    }

    private static void ApplyRawNumberTexts(BmsonDocument document, string json)
    {
        if (document == null || string.IsNullOrWhiteSpace(json))
        {
            return;
        }
        string infoJson = FindLastTopLevelPropertyValue(json, "info");
        if (!string.IsNullOrEmpty(infoJson) && document.Info != null)
        {
            document.Info.InitBpmText = NormalizeCanonicalJavaNumberText(FindLastObjectPropertyValue(infoJson, "init_bpm"));
        }
        string bpmEventsJson = FindLastTopLevelPropertyValue(json, "bpm_events");
        if (string.IsNullOrEmpty(bpmEventsJson) || document.BpmEvents == null)
        {
            return;
        }
        List<string> bpmTexts = ExtractBpmEventTexts(bpmEventsJson);
        int count = Math.Min(document.BpmEvents.Length, bpmTexts.Count);
        for (int index = 0; index < count; index++)
        {
            document.BpmEvents[index].BpmText = NormalizeCanonicalJavaNumberText(bpmTexts[index]);
        }
    }

    private static List<string> ExtractBpmEventTexts(string arrayJson)
    {
        List<string> result = new List<string>();
        int index = 0;
        SkipWhiteSpace(arrayJson, ref index);
        if (index >= arrayJson.Length || arrayJson[index] != '[')
        {
            return result;
        }
        index++;
        while (index < arrayJson.Length)
        {
            SkipWhiteSpace(arrayJson, ref index);
            if (index >= arrayJson.Length || arrayJson[index] == ']')
            {
                break;
            }
            int valueStart = index;
            int valueEnd = SkipJsonValue(arrayJson, valueStart);
            string itemJson = arrayJson.Substring(valueStart, valueEnd - valueStart);
            string bpmText = FindLastObjectPropertyValue(itemJson, "bpm");
            if (!string.IsNullOrEmpty(bpmText))
            {
                result.Add(bpmText);
            }
            index = valueEnd;
            SkipWhiteSpace(arrayJson, ref index);
            if (index < arrayJson.Length && arrayJson[index] == ',')
            {
                index++;
            }
        }
        return result;
    }

    private static string FindLastTopLevelPropertyValue(string json, string propertyName)
    {
        int index = 0;
        SkipWhiteSpace(json, ref index);
        if (index >= json.Length || json[index] != '{')
        {
            return null;
        }
        index++;
        string found = null;
        while (index < json.Length)
        {
            SkipWhiteSpace(json, ref index);
            if (index >= json.Length || json[index] == '}')
            {
                break;
            }
            string name = ReadJsonString(json, ref index);
            SkipWhiteSpace(json, ref index);
            if (index >= json.Length || json[index] != ':')
            {
                break;
            }
            index++;
            SkipWhiteSpace(json, ref index);
            int valueStart = index;
            int valueEnd = SkipJsonValue(json, valueStart);
            if (string.Equals(name, propertyName, StringComparison.Ordinal))
            {
                found = json.Substring(valueStart, valueEnd - valueStart);
            }
            index = valueEnd;
            SkipWhiteSpace(json, ref index);
            if (index < json.Length && json[index] == ',')
            {
                index++;
            }
        }
        return found;
    }

    private static string FindLastObjectPropertyValue(string json, string propertyName)
    {
        int index = 0;
        SkipWhiteSpace(json, ref index);
        if (index >= json.Length || json[index] != '{')
        {
            return null;
        }
        index++;
        string found = null;
        while (index < json.Length)
        {
            SkipWhiteSpace(json, ref index);
            if (index >= json.Length || json[index] == '}')
            {
                break;
            }
            string name = ReadJsonString(json, ref index);
            SkipWhiteSpace(json, ref index);
            if (index >= json.Length || json[index] != ':')
            {
                break;
            }
            index++;
            SkipWhiteSpace(json, ref index);
            int valueStart = index;
            int valueEnd = SkipJsonValue(json, valueStart);
            if (string.Equals(name, propertyName, StringComparison.Ordinal))
            {
                found = json.Substring(valueStart, valueEnd - valueStart);
            }
            index = valueEnd;
            SkipWhiteSpace(json, ref index);
            if (index < json.Length && json[index] == ',')
            {
                index++;
            }
        }
        return found;
    }

    private static string NormalizeCanonicalJavaNumberText(string text)
    {
        text = UnquoteJsonString(text)?.Trim();
        if (string.IsNullOrEmpty(text) || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0 || text[0] == '+')
        {
            return null;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue)
            && Math.Abs(doubleValue) >= 10000000.0)
        {
            return null;
        }
        int decimalIndex = text.IndexOf('.');
        if (decimalIndex < 0)
        {
            return null;
        }
        string fractional = text.Substring(decimalIndex + 1);
        if (fractional.Length == 0)
        {
            return null;
        }
        if (fractional.IndexOf("999999999999", StringComparison.Ordinal) >= 0)
        {
            return null;
        }
        return fractional.Length > 1 && fractional.EndsWith("0", StringComparison.Ordinal)
            ? null
            : text;
    }

    private static string UnquoteJsonString(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
        {
            try
            {
                return JsonConvert.DeserializeObject<string>(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return text;
    }

    private static string ReadJsonString(string json, ref int index)
    {
        if (index >= json.Length || json[index] != '"')
        {
            return null;
        }
        int start = index;
        index = SkipJsonString(json, index);
        try
        {
            return JsonConvert.DeserializeObject<string>(json.Substring(start, index - start));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int SkipJsonValue(string json, int index)
    {
        SkipWhiteSpace(json, ref index);
        if (index >= json.Length)
        {
            return index;
        }
        char value = json[index];
        if (value == '"')
        {
            return SkipJsonString(json, index);
        }
        if (value == '{' || value == '[')
        {
            return SkipJsonContainer(json, index);
        }
        while (index < json.Length && json[index] != ',' && json[index] != '}' && json[index] != ']')
        {
            index++;
        }
        return index;
    }

    private static int SkipJsonContainer(string json, int index)
    {
        char open = json[index];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        while (index < json.Length)
        {
            char value = json[index];
            if (value == '"')
            {
                index = SkipJsonString(json, index);
                continue;
            }
            if (value == open)
            {
                depth++;
            }
            else if (value == close)
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }
            index++;
        }
        return index;
    }

    private static int SkipJsonString(string json, int index)
    {
        index++;
        while (index < json.Length)
        {
            if (json[index] == '\\')
            {
                index += 2;
                continue;
            }
            if (json[index] == '"')
            {
                return index + 1;
            }
            index++;
        }
        return index;
    }

    private static void SkipWhiteSpace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }
}

internal sealed class BmsonIntJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        Type type = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return type == typeof(int);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        bool nullable = Nullable.GetUnderlyingType(objectType) != null;
        if (reader.TokenType == JsonToken.Null)
        {
            return nullable ? null : 0;
        }
        if (reader.TokenType == JsonToken.Integer)
        {
            return Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
        }
        if (reader.TokenType == JsonToken.Float)
        {
            return (int)Math.Truncate(Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture));
        }
        if (reader.TokenType == JsonToken.String)
        {
            string value = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(value))
            {
                return nullable ? null : 0;
            }
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            {
                return (int)Math.Truncate(doubleValue);
            }
        }
        throw new JsonSerializationException("Cannot convert bmson value to int: " + reader.Value);
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }
}

internal sealed class BmsonDoubleJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        Type type = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return type == typeof(double);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        bool nullable = Nullable.GetUnderlyingType(objectType) != null;
        if (reader.TokenType == JsonToken.Null)
        {
            return nullable ? null : 0.0;
        }
        if (reader.TokenType == JsonToken.Integer || reader.TokenType == JsonToken.Float)
        {
            return Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
        }
        if (reader.TokenType == JsonToken.String)
        {
            string value = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(value))
            {
                return nullable ? null : 0.0;
            }
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            {
                return doubleValue;
            }
        }
        throw new JsonSerializationException("Cannot convert bmson value to double: " + reader.Value);
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }

}

internal sealed class BmsonDocument
{
    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    [JsonProperty("info")]
    public BmsonInfo Info { get; set; } = new BmsonInfo();

    [JsonProperty("lines")]
    public BmsonBarLine[] Lines { get; set; } = Array.Empty<BmsonBarLine>();

    [JsonProperty("bpm_events")]
    public BmsonBpmEvent[] BpmEvents { get; set; } = Array.Empty<BmsonBpmEvent>();

    [JsonProperty("stop_events")]
    public BmsonStopEvent[] StopEvents { get; set; } = Array.Empty<BmsonStopEvent>();

    [JsonProperty("scroll_events")]
    public BmsonScrollEvent[] ScrollEvents { get; set; } = Array.Empty<BmsonScrollEvent>();

    [JsonProperty("sound_channels")]
    public BmsonSoundChannel[] SoundChannels { get; set; } = Array.Empty<BmsonSoundChannel>();

    [JsonProperty("key_channels")]
    public BmsonMineChannel[] KeyChannels { get; set; } = Array.Empty<BmsonMineChannel>();

    [JsonProperty("mine_channels")]
    public BmsonMineChannel[] MineChannels { get; set; } = Array.Empty<BmsonMineChannel>();

    [JsonProperty("bga")]
    public BmsonBga Bga { get; set; } = new BmsonBga();
}

internal sealed class BmsonInfo
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("subtitle")]
    public string Subtitle { get; set; } = string.Empty;

    [JsonProperty("genre")]
    public string Genre { get; set; } = string.Empty;

    [JsonProperty("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonProperty("subartists")]
    public string[] Subartists { get; set; } = Array.Empty<string>();

    [JsonProperty("mode_hint")]
    public string ModeHint { get; set; } = "beat-7k";

    [JsonProperty("chart_name")]
    public string ChartName { get; set; } = string.Empty;

    [JsonProperty("judge_rank")]
    public int JudgeRank { get; set; } = 100;

    [JsonProperty("total")]
    public double Total { get; set; } = 100.0;

    [JsonProperty("init_bpm")]
    public double InitBpm { get; set; }

    [JsonProperty("level")]
    public double? Level { get; set; } = 0.0;

    [JsonProperty("back_image")]
    public string BackImage { get; set; } = string.Empty;

    [JsonProperty("eyecatch_image")]
    public string EyecatchImage { get; set; } = string.Empty;

    [JsonProperty("banner_image")]
    public string BannerImage { get; set; } = string.Empty;

    [JsonProperty("preview_music")]
    public string PreviewMusic { get; set; } = string.Empty;

    [JsonProperty("resolution")]
    public double Resolution { get; set; } = 240.0;

    [JsonProperty("ln_type")]
    public int LnType { get; set; }

    [JsonIgnore]
    public string InitBpmText { get; set; }

    public void Normalize()
    {
        Title ??= string.Empty;
        Subtitle ??= string.Empty;
        Genre ??= string.Empty;
        Artist ??= string.Empty;
        Subartists ??= Array.Empty<string>();
        ModeHint ??= "beat-7k";
        ChartName ??= string.Empty;
        Level ??= 0.0;
        BackImage ??= string.Empty;
        EyecatchImage ??= string.Empty;
        BannerImage ??= string.Empty;
        PreviewMusic ??= string.Empty;
        if (Resolution <= 0)
        {
            Resolution = 240.0;
        }
    }
}

internal sealed class BmsonSoundChannel
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("notes")]
    public BmsonSoundNote[] Notes { get; set; } = Array.Empty<BmsonSoundNote>();

    public void Normalize()
    {
        Name ??= string.Empty;
        Notes ??= Array.Empty<BmsonSoundNote>();
    }
}

internal sealed class BmsonSoundNote
{
    [JsonProperty("x")]
    public int X { get; set; }

    [JsonProperty("y")]
    public int Y { get; set; }

    [JsonProperty("l")]
    public int Length { get; set; }

    [JsonProperty("c")]
    public bool Continue { get; set; }

    [JsonProperty("up")]
    public bool Up { get; set; }

    [JsonProperty("t")]
    public int Type { get; set; }
}

internal sealed class BmsonMineChannel
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("notes")]
    public BmsonMineNote[] Notes { get; set; } = Array.Empty<BmsonMineNote>();

    public void Normalize()
    {
        Name ??= string.Empty;
        Notes ??= Array.Empty<BmsonMineNote>();
    }
}

internal sealed class BmsonMineNote
{
    [JsonProperty("x")]
    public int X { get; set; }

    [JsonProperty("y")]
    public int Y { get; set; }

    [JsonProperty("damage")]
    public double Damage { get; set; }
}

internal sealed class BmsonBpmEvent
{
    [JsonProperty("y")]
    public int Y { get; set; }

    [JsonProperty("bpm")]
    public double Bpm { get; set; }

    [JsonIgnore]
    public string BpmText { get; set; }
}

internal sealed class BmsonStopEvent
{
    [JsonProperty("y")]
    public int Y { get; set; }

    [JsonProperty("duration")]
    public double Duration { get; set; }
}

internal sealed class BmsonScrollEvent
{
    [JsonProperty("y")]
    public int Y { get; set; }

    [JsonProperty("rate")]
    public double Rate { get; set; } = 1.0;
}

internal sealed class BmsonBarLine
{
    [JsonProperty("y")]
    public int Y { get; set; }
}

internal sealed class BmsonBga
{
    [JsonProperty("bga_header")]
    public BmsonBgaHeader[] BgaHeader { get; set; } = Array.Empty<BmsonBgaHeader>();

    [JsonProperty("bga_events")]
    public BmsonBgaNote[] BgaEvents { get; set; } = Array.Empty<BmsonBgaNote>();

    [JsonProperty("layer_events")]
    public BmsonBgaNote[] LayerEvents { get; set; } = Array.Empty<BmsonBgaNote>();

    [JsonProperty("poor_events")]
    public BmsonBgaNote[] PoorEvents { get; set; } = Array.Empty<BmsonBgaNote>();

    public void Normalize()
    {
        BgaHeader ??= Array.Empty<BmsonBgaHeader>();
        BgaEvents ??= Array.Empty<BmsonBgaNote>();
        LayerEvents ??= Array.Empty<BmsonBgaNote>();
        PoorEvents ??= Array.Empty<BmsonBgaNote>();
    }
}

internal sealed class BmsonBgaHeader
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed class BmsonBgaNote
{
    [JsonProperty("y")]
    public int Y { get; set; }
}
