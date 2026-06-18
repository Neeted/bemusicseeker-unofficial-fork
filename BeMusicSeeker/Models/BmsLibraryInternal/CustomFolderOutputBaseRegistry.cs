using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Properties;
using Newtonsoft.Json;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class CustomFolderOutputBaseEntry
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

internal static class CustomFolderOutputBaseRegistry
{
    internal static IReadOnlyList<string> ReadAdditionalBaseDirectories()
    {
        return DeserializeBaseDirectories(Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);
    }

    internal static IReadOnlyList<string> DeserializeBaseDirectories(string serialized)
    {
        return DeserializeBaseDirectories(serialized, throwOnInvalidJson: false);
    }

    internal static IReadOnlyList<string> DeserializeBaseDirectoriesStrict(string serialized)
    {
        return DeserializeBaseDirectories(serialized, throwOnInvalidJson: true);
    }

    private static IReadOnlyList<string> DeserializeBaseDirectories(string serialized, bool throwOnInvalidJson)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return [];
        }

        try
        {
            string[] paths = JsonConvert.DeserializeObject<string[]>(serialized) ?? [];
            return NormalizeBaseDirectories(paths);
        }
        catch (JsonException) when (!throwOnInvalidJson)
        {
            return [];
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("追加出力先設定の形式が壊れています。", nameof(serialized), ex);
        }
    }

    internal static string SerializeBaseDirectories(IEnumerable<string> paths)
    {
        return JsonConvert.SerializeObject(NormalizeBaseDirectories(paths));
    }

    internal static IReadOnlyList<string> NormalizeBaseDirectories(IEnumerable<string> paths)
    {
        return [.. (paths ?? [])
            .Select(NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyList<CustomFolderOutputBaseEntry> CreateAdditionalEntries()
    {
        return CreateAdditionalEntries(ReadAdditionalBaseDirectories());
    }

    internal static IReadOnlyList<CustomFolderOutputBaseEntry> CreateAdditionalEntries(IEnumerable<string> paths)
    {
        return [.. NormalizeBaseDirectories(paths)
            .Select(path => new CustomFolderOutputBaseEntry
            {
                Name = GetDirectoryDisplayName(path),
                Path = path
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))];
    }

    internal static string ResolveNormalOutputBaseDirectory(string savedBaseName)
    {
        return ResolveNormalOutputBaseDirectory(
            savedBaseName,
            Settings.Default.LR2CustomFolderOutputBaseDir,
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);
    }

    internal static string ResolveNormalOutputBaseDirectory(
        string savedBaseName,
        string defaultBaseDirectory,
        string serializedAdditionalBaseDirectories)
    {
        if (TryResolveNormalOutputBaseDirectory(
            savedBaseName,
            defaultBaseDirectory,
            serializedAdditionalBaseDirectories,
            out string outputBaseDirectory))
        {
            return outputBaseDirectory;
        }

        return defaultBaseDirectory;
    }

    internal static bool TryResolveNormalOutputBaseDirectory(
        string savedBaseName,
        string defaultBaseDirectory,
        string serializedAdditionalBaseDirectories,
        out string outputBaseDirectory)
    {
        outputBaseDirectory = null;
        string normalizedSavedName = NormalizeBaseName(savedBaseName);
        if (string.IsNullOrWhiteSpace(normalizedSavedName))
        {
            outputBaseDirectory = defaultBaseDirectory;
            return !string.IsNullOrWhiteSpace(outputBaseDirectory);
        }

        CustomFolderOutputBaseEntry entry = CreateAdditionalEntries(
                DeserializeBaseDirectories(serializedAdditionalBaseDirectories))
            .FirstOrDefault(item => string.Equals(item.Name, normalizedSavedName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(entry?.Path))
        {
            outputBaseDirectory = entry.Path;
            return true;
        }

        return false;
    }

    internal static string GetDisplayName(string savedBaseName)
    {
        return GetDisplayName(
            savedBaseName,
            Settings.Default.LR2CustomFolderOutputBaseDir,
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);
    }

    internal static string GetDisplayName(
        string savedBaseName,
        string defaultBaseDirectory,
        string serializedAdditionalBaseDirectories)
    {
        string normalizedSavedName = NormalizeBaseName(savedBaseName);
        if (!string.IsNullOrWhiteSpace(normalizedSavedName)
            && CreateAdditionalEntries(DeserializeBaseDirectories(serializedAdditionalBaseDirectories))
                .Any(entry => string.Equals(entry.Name, normalizedSavedName, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedSavedName;
        }

        return GetDirectoryDisplayName(defaultBaseDirectory);
    }

    internal static bool ContainsBaseName(string savedBaseName)
    {
        string normalizedSavedName = NormalizeBaseName(savedBaseName);
        return !string.IsNullOrWhiteSpace(normalizedSavedName)
            && CreateAdditionalEntries().Any(entry => string.Equals(entry.Name, normalizedSavedName, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeBaseName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? null
            : name.Trim().RemoveInvalidFileNameChars();
    }

    internal static string GetDirectoryDisplayName(string path)
    {
        string normalized = NormalizeDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        string trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    internal static string RenameLastDirectoryName(string path, string newName)
    {
        string normalizedPath = NormalizeDirectoryPath(path);
        string normalizedName = NormalizeBaseName(newName);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException("出力先フォルダが選択されていません。", nameof(path));
        }
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("出力先フォルダ名が空です。", nameof(newName));
        }

        string trimmed = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("ルートディレクトリの名前は変更できません。", nameof(path));
        }

        return NormalizeDirectoryPath(Path.Combine(parent, normalizedName));
    }

    internal static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
