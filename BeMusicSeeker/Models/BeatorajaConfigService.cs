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
        return !string.IsNullOrWhiteSpace(rootPath)
            && LongPathFileSystem.DirectoryExists(rootPath)
            && LongPathFileSystem.FileExists(GetConfigPath(rootPath))
            && (LongPathFileSystem.FileExists(Path.Combine(rootPath, "beatoraja.jar")) || LongPathFileSystem.FileExists(Path.Combine(rootPath, "beatoraja.exe")))
            && TryReadConfig(rootPath, out _);
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

    internal static void SyncTableUrls(string rootPath, IEnumerable<string> currentManagedUrls, IEnumerable<string> previousManagedUrls, Func<bool> shouldProceed = null)
    {
        if (!IsBeatorajaRootPathValid(rootPath))
        {
            return;
        }
        lock (ConfigWriteLock)
        {
            if (shouldProceed?.Invoke() == false)
            {
                return;
            }
            string configPath = GetConfigPath(rootPath);
            var config = JObject.Parse(ReadAllText(configPath, Encoding.UTF8));
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
                return;
            }
            if (shouldProceed?.Invoke() == false)
            {
                return;
            }
            config["tableURL"] = tableUrl;
            WriteConfigAtomic(configPath, config);
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
            config = JObject.Parse(ReadAllText(configPath, Encoding.UTF8));
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
