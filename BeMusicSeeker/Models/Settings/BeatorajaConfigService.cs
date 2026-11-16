using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

internal static class BeatorajaConfigService
{
    internal const string ConfigFileName = "config_sys.json";

    private static readonly object ConfigWriteLock = new();

    internal static bool IsBeatorajaRootPathValid(string rootPath)
    {
        return HasBeatorajaRootFiles(rootPath) && TryReadConfig(rootPath, out _);
    }

    private static bool HasBeatorajaRootFiles(string rootPath)
    {
        return !string.IsNullOrWhiteSpace(rootPath)
            && LongPathFileSystem.DirectoryExists(rootPath)
            && LongPathFileSystem.FileExists(GetConfigPath(rootPath))
            && (LongPathFileSystem.FileExists(Path.Combine(rootPath, "beatoraja.jar")) || LongPathFileSystem.FileExists(Path.Combine(rootPath, "beatoraja.exe")));
    }

    internal static string GetConfigPath(string rootPath)
    {
        return string.IsNullOrWhiteSpace(rootPath) ? null : Path.Combine(rootPath, ConfigFileName);
    }

    internal static string GetTablePath(string rootPath)
    {
        JObject config = ReadConfigOrDefault(rootPath);
        return ResolveConfiguredPath(rootPath, config.Value<string>("tablepath"), "table");
    }

    /// <summary>必要BMT出力の既存ルートから出力先を読みます。構成ファイルの失敗を無効判定や既定設定へ置換しません。</summary>
    /// <param name="rootPath">対象ルート。未指定や構成ファイル・実行ファイルがない対象は従来どおり対象外です。</param>
    /// <param name="tablePath">対象ルートの出力先。対象外なら空文字列。</param>
    /// <returns>必要出力の対象ルートならtrue。</returns>
    /// <exception cref="ConfigOperationException">対象の構成ファイルを読めないか、JSONとして解釈できません。</exception>
    internal static bool TryGetBmtTablePath(string rootPath, out string tablePath)
    {
        tablePath = string.Empty;
        if (!HasBeatorajaRootFiles(rootPath)) { return false; }
        JObject config = ReadConfigForBmt(rootPath);
        tablePath = ResolveConfiguredPath(rootPath, config.Value<string>("tablepath"), "table");
        return true;
    }

    internal static string GetPlayerRootPath(string rootPath)
    {
        JObject config = ReadConfigOrDefault(rootPath);
        return ResolveConfiguredPath(rootPath, config.Value<string>("playerpath"), "player");
    }

    internal static string GetConfiguredPlayerId(string rootPath)
    {
        JObject config = ReadConfigOrDefault(rootPath);
        return config.Value<string>("playername") ?? string.Empty;
    }

    internal static IReadOnlyList<string> ReadTableUrls(string rootPath)
    {
        JObject config = ReadConfigOrDefault(rootPath);
        return [.. (config["tableURL"] as JArray ?? [])
            .Select(token => token.Type == JTokenType.String ? token.Value<string>() : null)
            .Where(url => !string.IsNullOrWhiteSpace(url))];
    }

    internal static string GetScoreDbPath(string rootPath, string playerId)
    {
        return GetPlayerDbPath(rootPath, playerId, "score.db");
    }

    internal static string GetScoreLogDbPath(string rootPath, string playerId)
    {
        return GetPlayerDbPath(rootPath, playerId, "scorelog.db");
    }

    internal static string GetPlayerDbPath(string rootPath, string playerId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(playerId))
        {
            return string.Empty;
        }
        return Path.Combine(GetPlayerRootPath(rootPath), playerId, string.IsNullOrWhiteSpace(fileName) ? string.Empty : fileName);
    }

    internal static List<string> GetPlayerIds(string rootPath)
    {
        string playerRoot = GetPlayerRootPath(rootPath);
        if (string.IsNullOrWhiteSpace(playerRoot) || !LongPathFileSystem.DirectoryExists(playerRoot))
        {
            return [];
        }
        return [.. LongPathFileSystem.EnumerateDirectories(playerRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static bool IsPlayerScoreDbPathValid(string rootPath, string playerId)
    {
        string scoreDbPath = GetScoreDbPath(rootPath, playerId);
        return !string.IsNullOrWhiteSpace(scoreDbPath)
            && string.Equals(Path.GetFileName(scoreDbPath), "score.db", StringComparison.OrdinalIgnoreCase)
            && LongPathFileSystem.FileExists(scoreDbPath);
    }

    /// <summary>既存ルートの管理URLを原子的に同期し、構成ファイルの読取り・解析・置換失敗を対象パスと元原因付きで返します。</summary>
    /// <returns>現在集合に一致するか原子的な書込みが成功した場合はtrue。対象外rootまたは継続条件の取消で何もしなかった場合はfalse。</returns>
    internal static bool SyncTableUrls(string rootPath, IEnumerable<string> currentManagedUrls, IEnumerable<string> previousManagedUrls, Func<bool> shouldProceed = null)
    {
        if (!HasBeatorajaRootFiles(rootPath))
        {
            return false;
        }
        lock (ConfigWriteLock)
        {
            if (shouldProceed?.Invoke() == false)
            {
                return false;
            }
            string configPath = GetConfigPath(rootPath);
            JObject config = ReadConfigForBmt(rootPath);
            var managedUrlSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string url in (previousManagedUrls ?? []).Concat(currentManagedUrls ?? []))
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    managedUrlSet.Add(url);
                }
            }
            List<string> existingUrls = [.. (config["tableURL"] as JArray ?? [])
                .Select(token => token.Type == JTokenType.String ? token.Value<string>() : null)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Where(url => !managedUrlSet.Contains(url))
                .Distinct(StringComparer.Ordinal)];
            List<string> appendedUrls = [.. (currentManagedUrls ?? [])
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.Ordinal)];
            var tableUrl = new JArray(existingUrls.Concat(appendedUrls));
            if (JToken.DeepEquals(config["tableURL"], tableUrl))
            {
                return true;
            }
            if (shouldProceed?.Invoke() == false)
            {
                return false;
            }
            config["tableURL"] = tableUrl;
            WriteConfigAtomic(configPath, config);
            return true;
        }
    }

    private static bool TryReadConfig(string rootPath, out JObject config)
    {
        config = null;
        string configPath = GetConfigPath(rootPath);
        if (string.IsNullOrWhiteSpace(configPath) || !LongPathFileSystem.FileExists(configPath))
        {
            return false;
        }
        try
        {
            config = ReadConfig(configPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JObject ReadConfigOrDefault(string rootPath)
    {
        return TryReadConfig(rootPath, out JObject config) ? config : [];
    }

    private static JObject ReadConfigForBmt(string rootPath)
    {
        string configPath = GetConfigPath(rootPath);
        try { return ReadConfig(configPath); }
        catch (Exception exception) { throw new ConfigOperationException(configPath, exception); }
    }

    // 外部設定の任意文字列は日時へ変換せず、URL同期後も元の型と値を保つ。
    private static JObject ReadConfig(string configPath)
    {
        using var reader = new JsonTextReader(new StringReader(ReadAllText(configPath, Encoding.UTF8))) { DateParseHandling = DateParseHandling.None };
        var config = JObject.Load(reader);
        // JObject.Parseと同じく末尾まで読み、後続の別JSONを許容しない。
        while (reader.Read()) { }
        return config;
    }

    /// <summary>必要BMT操作の構成ファイル失敗について、通知対象パスと元例外を保持します。</summary>
    internal sealed class ConfigOperationException : IOException
    {
        /// <summary>失敗した構成ファイルのパスと元原因を捕捉します。</summary>
        internal ConfigOperationException(string configPath, Exception cause) : base(cause.Message, cause)
        {
            ConfigPath = configPath;
        }

        /// <summary>通知へ渡す失敗対象の構成ファイルパス。</summary>
        internal string ConfigPath { get; }
    }

    private static string ResolveConfiguredPath(string rootPath, string configuredPath, string defaultPath)
    {
        string path = string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath;
        if (Path.IsPathRooted(path))
        {
            return LongPathFileSystem.NormalizePathForStorage(path);
        }
        return LongPathFileSystem.NormalizePathForStorage(Path.Combine(rootPath ?? string.Empty, path));
    }

    private static void WriteConfigAtomic(string configPath, JObject config)
    {
        string tempPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteAllText(tempPath, config.ToString(Formatting.Indented), new UTF8Encoding(false));
            LongPathFileSystem.MoveFile(tempPath, configPath, overwrite: true);
        }
        catch (Exception exception) { throw new ConfigOperationException(configPath, exception); }
        finally
        {
            try
            {
                if (LongPathFileSystem.FileExists(tempPath))
                {
                    LongPathFileSystem.DeleteFile(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static string ReadAllText(string path, Encoding encoding)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteAllText(string path, string contents, Encoding encoding)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(contents);
    }
}
