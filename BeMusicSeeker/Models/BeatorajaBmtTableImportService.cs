using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

internal static class BeatorajaBmtTableImportService
{
    private static readonly Regex LevelNumberRegex = new("([+-]?\\d+(?:\\.\\d*)?|\\.\\d+)", RegexOptions.Compiled);

    internal static BMSTable LoadCachedTable(string tablePath, string configTableUrl)
    {
        if (string.IsNullOrWhiteSpace(configTableUrl) || !Uri.TryCreate(configTableUrl, UriKind.Absolute, out Uri configTableUri))
        {
            throw new ArgumentException("configTableUrl must be an absolute URI.", nameof(configTableUrl));
        }
        if (configTableUri == null || !configTableUri.IsAbsoluteUri)
        {
            throw new ArgumentException("configTableUri must be an absolute URI.", nameof(configTableUri));
        }
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            throw new ArgumentException("tablePath is empty.", nameof(tablePath));
        }

        string cachePath = GetCachedTablePath(tablePath, configTableUrl);
        if (!LongPathFileSystem.FileExists(cachePath))
        {
            throw new FileNotFoundException("beatoraja .bmt cache was not found.", cachePath);
        }

        JObject tableData = ReadCachedTableData(cachePath);
        return CreateTable(tableData, configTableUrl);
    }

    internal static string GetCachedTablePath(string tablePath, string configTableUrl)
    {
        if (string.IsNullOrWhiteSpace(configTableUrl) || !Uri.TryCreate(configTableUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("configTableUrl must be an absolute URI.", nameof(configTableUrl));
        }
        return Path.Combine(tablePath ?? string.Empty, BMSTable.ComputeSha256Hex(configTableUrl) + ".bmt");
    }

    internal static JObject ReadCachedTableData(string cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            throw new ArgumentException("cachePath is empty.", nameof(cachePath));
        }

        using FileStream fileStream = LongPathFileSystem.OpenRead(cachePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JObject.Parse(reader.ReadToEnd());
    }

    internal static BMSTable CreateTable(JObject tableData, string configTableUrl)
    {
        if (tableData == null)
        {
            throw new ArgumentNullException(nameof(tableData));
        }
        if (string.IsNullOrWhiteSpace(configTableUrl) || !Uri.TryCreate(configTableUrl, UriKind.Absolute, out Uri configTableUri))
        {
            throw new ArgumentException("configTableUrl must be an absolute URI.", nameof(configTableUrl));
        }

        string name = tableData.Value<string>("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PlaylistHeaderParseException("beatoraja .bmt table name is empty.");
        }

        List<JObject> folders = [.. (tableData["folder"] as JArray ?? [])
            .OfType<JObject>()];
        JArray courses = CloneObjectArray(tableData["course"]);
        if (folders.Count == 0 && courses.Count == 0)
        {
            throw new PlaylistDataParseException("beatoraja .bmt table has no folder or course.");
        }

        List<string> folderNames = [.. folders
            .Select(folder => folder.Value<string>("name") ?? string.Empty)
            .Distinct(StringComparer.Ordinal)];
        string tag = tableData.Value<string>("tag") ?? string.Empty;
        JObject header = new()
        {
            ["name"] = name,
            ["symbol"] = tag,
            ["tag"] = tag,
            ["data_url"] = configTableUrl,
            ["compat_prefix"] = InferCompatPrefix(tag, folderNames),
            ["folder_order"] = new JArray(folderNames),
            ["course"] = courses
        };
        JArray data = BuildDataRows(folders);

        var table = new BMSTable();
        table.LoadHeaderJSON(header.ToString(Formatting.None), configTableUri, configTableUri);
        table.LoadDataJSON(data.ToString(Formatting.None));
        return table;
    }

    private static string InferCompatPrefix(string tag, IEnumerable<string> folderNames)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return string.Empty;
        }
        List<string> nonEmptyFolderNames = [.. (folderNames ?? []).Where(name => !string.IsNullOrEmpty(name))];
        return nonEmptyFolderNames.Count > 0
            && nonEmptyFolderNames.All(name => name.StartsWith(tag, StringComparison.Ordinal))
                ? tag
                : string.Empty;
    }

    private static JArray BuildDataRows(IEnumerable<JObject> folders)
    {
        JArray rows = [];
        foreach (JObject folder in folders ?? [])
        {
            string folderName = folder.Value<string>("name") ?? string.Empty;
            double? level = ExtractLevelNumber(folderName);
            foreach (JObject song in (folder["songs"] as JArray ?? []).OfType<JObject>())
            {
                JObject row = BuildDataRow(song, folderName, level);
                if (row != null)
                {
                    rows.Add(row);
                }
            }
        }
        return rows;
    }

    private static JObject BuildDataRow(JObject song, string folderName, double? level)
    {
        string title = song?.Value<string>("title");
        string md5 = song?.Value<string>("md5");
        string sha256 = song?.Value<string>("sha256");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        var row = new JObject
        {
            ["title"] = title ?? string.Empty,
            ["folder"] = folderName ?? string.Empty
        };
        if (level.HasValue)
        {
            row["org_level"] = level.Value;
        }
        AddIfNotEmpty(row, "artist", song.Value<string>("artist"));
        AddIfNotEmpty(row, "md5", md5);
        AddIfNotEmpty(row, "sha256", sha256);
        AddIfNotEmpty(row, "url", song.Value<string>("url"));
        AddIfNotEmpty(row, "url_diff", FirstNonEmpty(song.Value<string>("appendurl"), song.Value<string>("url_diff")));
        JArray orgMd5 = NormalizeOrgMd5(song["org_md5"]);
        if (orgMd5.Count > 0)
        {
            row["org_md5s"] = orgMd5;
        }
        return row;
    }

    private static double? ExtractLevelNumber(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }
        Match match = LevelNumberRegex.Match(folderName);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double level)
            ? level
            : null;
    }

    private static JArray CloneObjectArray(JToken source)
    {
        JArray result = [];
        foreach (JObject item in (source as JArray ?? []).OfType<JObject>())
        {
            result.Add(item.DeepClone());
        }
        if (source is JObject singleObject)
        {
            result.Add(singleObject.DeepClone());
        }
        return result;
    }

    private static JArray NormalizeOrgMd5(JToken source)
    {
        JArray result = [];
        if (source is JArray array)
        {
            foreach (JToken item in array)
            {
                AddOrgMd5(result, item?.ToString());
            }
            return result;
        }
        AddOrgMd5(result, source?.ToString());
        return result;
    }

    private static void AddOrgMd5(JArray result, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            result.Add(value);
        }
    }

    private static void AddIfNotEmpty(JObject target, string propertyName, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[propertyName] = value;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
