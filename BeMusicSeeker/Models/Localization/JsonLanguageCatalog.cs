using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Text;
using System.Xml;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Localization;

/// <summary>既受理のJSON辞書を読み、既知の入力・読取り失敗だけを日本語資源へのfallbackへ渡します。</summary>
public static class JsonLanguageCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object DiscoveryGate = new();

    private static ReadOnlyDictionary<string, string> discoveredLanguages;

    /// <summary>完了した探索だけを共有し、既知の走査失敗では今回限りの日本語候補を返します。</summary>
    internal static ReadOnlyDictionary<string, string> GetLanguagesSnapshot()
    {
        lock (DiscoveryGate)
        {
            if (discoveredLanguages != null)
            {
                return discoveredLanguages;
            }
            Dictionary<string, string> languages = DiscoverLanguages(GetLangDirectory(), out bool scanCompleted);
            var snapshot = new ReadOnlyDictionary<string, string>(languages);
            if (scanCompleted)
            {
                discoveredLanguages = snapshot;
            }
            return snapshot;
        }
    }

    /// <summary>日本語または完了した探索で見つかったカルチャが候補に含まれるかを返します。</summary>
    internal static bool ContainsCulture(string cultureName)
        => !string.IsNullOrWhiteSpace(cultureName)
            && GetLanguagesSnapshot().Values.Contains(cultureName, StringComparer.OrdinalIgnoreCase);

    /// <summary>指定カルチャの辞書を次の取得で読み直せるようにします。</summary>
    public static void Invalidate(string cultureName)
    {
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            Cache.TryRemove(cultureName, out IReadOnlyDictionary<string, string> _);
        }
    }

    /// <summary>次の候補取得で言語ディレクトリを再探索します。</summary>
    internal static void InvalidateLanguages()
    {
        lock (DiscoveryGate)
        {
            discoveredLanguages = null;
        }
    }

    /// <summary>指定JSONの値を優先して返します。未知の故障は空辞書としてキャッシュしません。</summary>
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
        => LoadLanguageMapFromFile(Path.Combine(GetLangDirectory(), cultureName + ".json"));

    /// <summary>実ファイルを従来のserializerで読み、欠落・破損・アクセス失敗だけを空辞書へ変換します。</summary>
    /// <remarks>UTF-8/BOM、任意のキーとserializerが従来受理した値を維持し、診断失敗は読取り結果を変えません。</remarks>
    internal static IReadOnlyDictionary<string, string> LoadLanguageMapFromFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        string json;
        try
        {
            json = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            ReportLanguageFailure(ex, "lang_json read failed path=" + filePath);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> dictionary;
        try
        {
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }
            using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>), settings);
            dictionary = serializer.ReadObject(memoryStream) as Dictionary<string, string>;
        }
        catch (Exception ex) when (ex is SerializationException or XmlException
            || ex.GetType() == typeof(ArgumentException))
        {
            // simple dictionaryの重複キーも入力失敗です。契約型の故障は捕捉しません。
            ReportLanguageFailure(ex, "lang_json invalid_data path=" + filePath);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (dictionary == null)
        {
            ReportLanguageFailure(null, "lang_json invalid_data path=" + filePath);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var result = new Dictionary<string, string>(dictionary, StringComparer.Ordinal);
        try
        {
            NLogWrapper.GetLogger(typeof(JsonLanguageCatalog)).Info("lang_json loaded culture=" + Path.GetFileNameWithoutExtension(filePath) + " keys=" + result.Count);
        }
        catch (Exception)
        {
            // 任意診断の故障で、正常に読めた辞書を空辞書へ置き換えません。
        }
        return result;
    }

    /// <summary>
    /// lang/内のJSONから表示名とカルチャ名を探索します。日本語はJSONなしでも候補に含みます。
    /// 既知の走査失敗は日本語候補へ戻し、未知の故障は伝播します。
    /// </summary>
    public static Dictionary<string, string> DiscoverLanguages()
        => DiscoverLanguages(GetLangDirectory());

    /// <summary>指定ディレクトリの実探索を行い、正常ファイルと既知の失敗ファイルを独立して扱います。</summary>
    internal static Dictionary<string, string> DiscoverLanguages(string languageDirectory)
        => DiscoverLanguages(languageDirectory, out _);

    private static Dictionary<string, string> DiscoverLanguages(string languageDirectory, out bool scanCompleted)
    {
        scanCompleted = false;
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Default(日本語)", "ja-JP" }
        };
        string[] filePaths;
        try
        {
            filePaths = Directory.GetFiles(languageDirectory, "*.json");
        }
        catch (DirectoryNotFoundException ex)
        {
            ReportLanguageFailure(ex, "lang_json discover: lang directory not found path=" + languageDirectory);
            scanCompleted = true;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            ReportLanguageFailure(ex, "lang_json discover: failed to scan path=" + languageDirectory);
            return result;
        }

        foreach (string filePath in filePaths)
        {
            string cultureName = Path.GetFileNameWithoutExtension(filePath);
            try
            {
                CultureInfo.GetCultureInfo(cultureName);
            }
            catch (CultureNotFoundException ex)
            {
                ReportLanguageFailure(ex, "lang_json discover: invalid culture path=" + filePath);
                continue;
            }
            IReadOnlyDictionary<string, string> dictionary = LoadLanguageMapFromFile(filePath);
            if (dictionary.TryGetValue("_language_name", out string displayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                if (!result.ContainsKey(displayName))
                {
                    result[displayName] = cultureName;
                }
            }
            else
            {
                ReportLanguageFailure(null, "lang_json discover: _language_name not found path=" + filePath);
            }
        }
        scanCompleted = true;
        return result;
    }

    private static void ReportLanguageFailure(Exception exception, string message)
    {
        try
        {
            NLogWrapper.GetLogger(typeof(JsonLanguageCatalog)).Warn(exception, message);
        }
        catch (Exception)
        {
            // 診断は任意であり、読取り・探索の確定した結果と元原因を変えません。
        }
    }

    /// <summary>アプリケーションが所有するlang/のパスを返します。</summary>
    private static string GetLangDirectory()
        => ApplicationPathPolicy.Current.LanguageDirectory;
}
