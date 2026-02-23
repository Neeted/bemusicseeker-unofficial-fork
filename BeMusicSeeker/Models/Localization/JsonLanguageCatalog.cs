using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Localization;

public static class JsonLanguageCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate(string cultureName)
    {
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            Cache.TryRemove(cultureName, out var _);
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
            string location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string text = Path.Combine(location, "lang", cultureName + ".json");
            if (!File.Exists(text))
            {
                NLogWrapper.TraceLogger?.Warn("lang_json missing path=" + text);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            using FileStream stream = File.OpenRead(text);
            DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
            DataContractJsonSerializer dataContractJsonSerializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>), settings);
            Dictionary<string, string> dictionary = dataContractJsonSerializer.ReadObject(stream) as Dictionary<string, string>;
            if (dictionary == null)
            {
                NLogWrapper.TraceLogger?.Warn("lang_json invalid_data path=" + text);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return new Dictionary<string, string>(dictionary, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "lang_json load failed culture=" + cultureName);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
