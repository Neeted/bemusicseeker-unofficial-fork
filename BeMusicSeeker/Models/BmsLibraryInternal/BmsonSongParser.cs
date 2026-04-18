using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.LR2;
using Codeplex.Data;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class BmsonSongParser
{
    private static readonly Regex namedFileRegex = new Regex("\"name\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static LR2SongDBExtended.bmson_song Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        string fullPath = Path.GetFullPath(filePath);
        string json = File.ReadAllText(fullPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
        dynamic root = DynamicJson.Parse(json);
        dynamic info = null;
        if (root != null && root.IsDefined("info") && root.info != null)
        {
            info = root.info;
        }

        string subtitle = ComposeSubtitle(ReadString(info, "subtitle"), ReadString(info, "chart_name"));
        string artist = ComposeArtist(ReadString(info, "artist"), ReadStringArray(info, "subartists"));
        DateTime updatedAt = File.GetLastWriteTimeUtc(fullPath);

        LR2SongDBExtended.bmson_song result = new LR2SongDBExtended.bmson_song
        {
            path = fullPath,
            folder = Path.GetDirectoryName(fullPath) ?? string.Empty,
            title = ReadString(info, "title"),
            subtitle = subtitle,
            artist = artist,
            genre = ReadString(info, "genre"),
            level = ReadNullableDouble(info, "level"),
            mode_hint = ReadString(info, "mode_hint"),
            md5 = ComputeHash(fullPath, MD5.Create()),
            sha256 = BMSFile.GetSHA256Hash(fullPath),
            banner = ReadString(info, "banner_image"),
            backbmp = ReadString(info, "back_image"),
            stagefile = ReadString(info, "eyecatch_image"),
            preview_music = ReadString(info, "preview_music"),
            updated_at = updatedAt
        };
        result.wav_files = ReadBmsonWavFiles(json, result.preview_music);
        result.bga_files = ReadBmsonBgaFiles(json);
        return result;
    }

    internal static int? ResolvePlaylistMode(string modeHint)
    {
        string normalized = string.IsNullOrWhiteSpace(modeHint) ? string.Empty : modeHint.Trim().ToLowerInvariant();
        return normalized switch
        {
            "beat-5k" => 5,
            "beat-7k" => 7,
            "beat-10k" => 10,
            "beat-14k" => 14,
            "popn-5k" => 9,
            "popn-9k" => 9,
            "keyboard-24k" => 24,
            "keyboard-24k-double" => 48,
            _ => null
        };
    }

    internal static string ComposeDisplayTitle(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(song.subtitle))
        {
            return song.title ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(song.title))
        {
            return song.subtitle ?? string.Empty;
        }
        return song.title + " " + song.subtitle;
    }

    internal static string ComposeDisplayFolder(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return string.Empty;
        }
        string folderPath = !string.IsNullOrWhiteSpace(song.folder) ? song.folder : Path.GetDirectoryName(song.path);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }
        string trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) ?? string.Empty;
    }

    private static string ReadString(dynamic obj, string propertyName)
    {
        if (obj == null || propertyName == null)
        {
            return string.Empty;
        }
        try
        {
            if (!obj.IsDefined(propertyName))
            {
                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
        try
        {
            return propertyName switch
            {
                "title" => obj.title?.ToString() ?? string.Empty,
                "subtitle" => obj.subtitle?.ToString() ?? string.Empty,
                "chart_name" => obj.chart_name?.ToString() ?? string.Empty,
                "artist" => obj.artist?.ToString() ?? string.Empty,
                "genre" => obj.genre?.ToString() ?? string.Empty,
                "name" => obj.name?.ToString() ?? string.Empty,
                "mode_hint" => obj.mode_hint?.ToString() ?? string.Empty,
                "banner_image" => obj.banner_image?.ToString() ?? string.Empty,
                "back_image" => obj.back_image?.ToString() ?? string.Empty,
                "eyecatch_image" => obj.eyecatch_image?.ToString() ?? string.Empty,
                "preview_music" => obj.preview_music?.ToString() ?? string.Empty,
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(dynamic obj, string propertyName)
    {
        if (obj == null || propertyName == null)
        {
            return Array.Empty<string>();
        }
        try
        {
            if (!obj.IsDefined(propertyName))
            {
                return Array.Empty<string>();
            }
            if (propertyName == "subartists" && obj.subartists != null)
            {
                object value = obj.subartists;
                if (value is object[] array)
                {
                    return array
                        .Where((object item) => item != null)
                        .Select((object item) => item.ToString())
                        .Where((string item) => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                }
                if (value is IEnumerable enumerable && value is not string)
                {
                    List<string> values = new List<string>();
                    foreach (object item in enumerable)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
                        {
                            values.Add(item.ToString());
                        }
                    }
                    if (values.Count > 0)
                    {
                        return values;
                    }
                }
                string scalar = value.ToString();
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    string trimmed = scalar.Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        try
                        {
                            object[] parsedArray = (object[])DynamicJson.Parse(trimmed);
                            return parsedArray
                                .Where((object item) => item != null)
                                .Select((object item) => item.ToString())
                                .Where((string item) => !string.IsNullOrWhiteSpace(item))
                                .ToArray();
                        }
                        catch
                        {
                        }
                    }
                    return new[] { scalar };
                }
            }
        }
        catch
        {
        }
        return Array.Empty<string>();
    }

    private static double? ReadNullableDouble(dynamic obj, string propertyName)
    {
        if (obj == null || propertyName == null)
        {
            return null;
        }
        try
        {
            if (!obj.IsDefined(propertyName))
            {
                return null;
            }
            object value = propertyName switch
            {
                "level" => obj.level,
                _ => null
            };
            if (value == null)
            {
                return null;
            }
            if (double.TryParse(value.ToString(), out double parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string ComposeSubtitle(string subtitle, string chartName)
    {
        string safeSubtitle = subtitle ?? string.Empty;
        string safeChartName = chartName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(safeChartName))
        {
            return safeSubtitle;
        }
        if (string.IsNullOrWhiteSpace(safeSubtitle))
        {
            return "[" + safeChartName + "]";
        }
        return safeSubtitle + " [" + safeChartName + "]";
    }

    private static string ComposeArtist(string artist, IReadOnlyList<string> subartists)
    {
        string safeArtist = artist ?? string.Empty;
        string safeSubartists = string.Join(",", (subartists ?? Array.Empty<string>()).Where((string item) => !string.IsNullOrWhiteSpace(item)));
        if (string.IsNullOrWhiteSpace(safeArtist))
        {
            return safeSubartists;
        }
        if (string.IsNullOrWhiteSpace(safeSubartists))
        {
            return safeArtist;
        }
        return safeArtist + " " + safeSubartists;
    }

    private static List<string> ReadBmsonWavFiles(string json, string previewMusic)
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalizedComponentPath(files, previewMusic);
        foreach (string componentPath in EnumerateNamedComponentPaths(json))
        {
            string extension = Path.GetExtension(componentPath);
            if (!string.IsNullOrWhiteSpace(extension) && BMSFile.wavExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(componentPath);
            }
        }
        return files.OrderBy((string item) => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ReadBmsonBgaFiles(string json)
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string componentPath in EnumerateNamedComponentPaths(json))
        {
            string extension = Path.GetExtension(componentPath);
            if (!string.IsNullOrWhiteSpace(extension) && BMSFile.bgaAllExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(componentPath);
            }
        }
        return files.OrderBy((string item) => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateNamedComponentPaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Enumerable.Empty<string>();
        }
        List<string> results = new List<string>();
        foreach (Match match in namedFileRegex.Matches(json))
        {
            string normalized = NormalizeComponentPath(Regex.Unescape(match.Groups["value"].Value));
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                results.Add(normalized);
            }
        }
        return results;
    }

    private static void AddNormalizedComponentPath(ISet<string> files, string filePath)
    {
        string normalized = NormalizeComponentPath(filePath);
        if (files == null || string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }
        files.Add(normalized);
    }

    private static string NormalizeComponentPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }
        return filePath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string ComputeHash(string filePath, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] hash = algorithm.ComputeHash(stream);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
