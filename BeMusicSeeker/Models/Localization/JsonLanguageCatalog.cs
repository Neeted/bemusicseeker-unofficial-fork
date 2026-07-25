using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Localization;

public static class JsonLanguageCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object DiscoveryGate = new();

    private static ReadOnlyDictionary<string, string> discoveredLanguages;

    internal static ReadOnlyDictionary<string, string> GetLanguagesSnapshot()
    {
        lock (DiscoveryGate)
        {
            return discoveredLanguages ??= new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(DiscoverLanguages(), StringComparer.Ordinal));
        }
    }

    internal static bool ContainsCulture(string cultureName)
        => !string.IsNullOrWhiteSpace(cultureName)
            && GetLanguagesSnapshot().Values.Contains(cultureName, StringComparer.OrdinalIgnoreCase);

    public static void Invalidate(string cultureName)
    {
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            Cache.TryRemove(cultureName, out IReadOnlyDictionary<string, string> _);
        }
    }

    internal static void InvalidateLanguages()
    {
        lock (DiscoveryGate)
        {
            discoveredLanguages = null;
        }
    }

    public static bool TryGetString(string cultureName, string key, out string value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(cultureName) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        IReadOnlyDictionary<string, string> dictionary = Cache.GetOrAdd(cultureName, LoadLanguageMap);
        return dictionary.TryGetValue(key, out value);
    }

    private static IReadOnlyDictionary<string, string> LoadLanguageMap(string cultureName)
    {
        try
        {
            string text = Path.Combine(GetLangDirectory(), cultureName + ".json");
            if (!File.Exists(text))
            {
                NLogWrapper.TraceLogger?.Warn("lang_json missing path=" + text);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            Dictionary<string, string> dictionary = ReadLanguageDictionary(text);
            if (dictionary == null)
            {
                NLogWrapper.TraceLogger?.Warn("lang_json invalid_data path=" + text);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            Ribbit.Logging.NLogWrapper.FileLogger?.Info($"[JsonLanguageCatalog] Loaded {dictionary.Count} keys for culture '{cultureName}'");
            return new Dictionary<string, string>(dictionary, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "lang_json load failed culture=" + cultureName);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// lang/ フォルダ内のJSONファイルをスキャンし、利用可能な言語の辞書を返す。
    /// キー: 表示名（_language_name の値）、値: カルチャ名（ファイル名から取得）。
    /// 日本語は常に先頭に含まれる（JSONファイル不要）。
    /// </summary>
    public static Dictionary<string, string> DiscoverLanguages()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Default(日本語)", "ja-JP" }
        };

        try
        {
            string langDir = GetLangDirectory();
            if (!Directory.Exists(langDir))
            {
                NLogWrapper.TraceLogger?.Warn("lang_json discover: lang directory not found path=" + langDir);
                return result;
            }

            foreach (string filePath in Directory.GetFiles(langDir, "*.json"))
            {
                try
                {
                    string cultureName = Path.GetFileNameWithoutExtension(filePath);
                    // カルチャ名が有効かどうかを検証
                    System.Globalization.CultureInfo.GetCultureInfo(cultureName);

                    Dictionary<string, string> dictionary = ReadLanguageDictionary(filePath);

                    if (dictionary != null && dictionary.TryGetValue("_language_name", out string displayName) && !string.IsNullOrWhiteSpace(displayName))
                    {
                        // 同じ表示名がなければ追加
                        if (!result.ContainsKey(displayName))
                        {
                            result[displayName] = cultureName;
                        }
                    }
                    else
                    {
                        NLogWrapper.TraceLogger?.Warn("lang_json discover: _language_name not found in " + filePath);
                    }
                }
                catch (Exception ex)
                {
                    NLogWrapper.TraceLogger?.Warn(ex, "lang_json discover: failed to read " + filePath);
                }
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "lang_json discover: failed to scan lang directory");
        }

        return result;
    }

    private static Dictionary<string, string> ReadLanguageDictionary(string filePath)
    {
        string json = File.ReadAllText(filePath, Encoding.UTF8);
        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            json = json.Substring(1);
        }
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>), settings);
        return serializer.ReadObject(memoryStream) as Dictionary<string, string>;
    }

    /// <summary>
    /// lang/ ディレクトリのパスを返す
    /// </summary>
    private static string GetLangDirectory()
    {
        return ApplicationPathPolicy.Current.LanguageDirectory;
    }
}
