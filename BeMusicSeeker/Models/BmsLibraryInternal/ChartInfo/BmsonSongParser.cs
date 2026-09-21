using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class BmsonSongParser
{
    // Path-based parser entry point. New single-read flows should pass a ChartFileSnapshot
    // to ParseSnapshot so lightweight metadata and chart_info can share the same bytes.
    public static LR2SongDBExtended.bmson_song Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        string fullPath = LongPathFileSystem.NormalizePathForStorage(filePath);
        string json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(LongPathFileSystem.ReadAllBytes(fullPath));
        BmsonDocument root = BmsonJsonParser.Parse(json);
        DateTime updatedAt = LongPathFileSystem.GetLastWriteTimeUtc(fullPath);
        return CreateSong(
            fullPath,
            root,
            ComputeHash(fullPath, MD5.Create()),
            BMSFile.GetSHA256Hash(fullPath),
            updatedAt);
    }

    internal static LR2SongDBExtended.bmson_song ParseSnapshot(ChartFileSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        string json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(snapshot.Bytes);
        BmsonDocument root = BmsonJsonParser.Parse(json);
        return CreateSong(snapshot.Path, root, snapshot.Md5, snapshot.Sha256, snapshot.LastWriteTimeUtc);
    }

    private static LR2SongDBExtended.bmson_song CreateSong(
        string fullPath,
        BmsonDocument root,
        string md5,
        string sha256,
        DateTime updatedAt)
    {
        BmsonInfo info = root?.Info ?? new BmsonInfo();
        var unsupportedReferences = new List<UnsupportedChartResourceReference>();
        var result = new LR2SongDBExtended.bmson_song
        {
            path = fullPath,
            folder = Path.GetDirectoryName(fullPath) ?? string.Empty,
            title = info.Title ?? string.Empty,
            subtitle = ComposeSubtitle(info.Subtitle, info.ChartName),
            artist = ComposeArtist(info.Artist, info.Subartists ?? []),
            genre = info.Genre ?? string.Empty,
            level = info.Level.HasValue ? (double?)info.Level.Value : null,
            mode_hint = info.ModeHint ?? string.Empty,
            md5 = md5,
            sha256 = sha256,
            banner = NormalizeComponentPath(info.BannerImage, unsupportedReferences, ChartResourceKind.Image),
            backbmp = NormalizeComponentPath(info.BackImage, unsupportedReferences, ChartResourceKind.Image),
            stagefile = NormalizeComponentPath(info.EyecatchImage, unsupportedReferences, ChartResourceKind.Image),
            preview_music = NormalizeComponentPath(info.PreviewMusic, unsupportedReferences, ChartResourceKind.Audio),
            updated_at = updatedAt
        };
        result.wav_files = ReadBmsonWavFiles(root, result.preview_music, unsupportedReferences);
        result.bga_files = ReadBmsonBgaFiles(root, unsupportedReferences);
        result.UnsupportedResourceReferences = unsupportedReferences;
        result.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(result.path, result.md5);
        result.HasFreshResourceReferences = true;
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

    private static List<string> ReadBmsonWavFiles(BmsonDocument document, string previewMusic, ICollection<UnsupportedChartResourceReference> unsupportedReferences)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalizedComponentPath(files, previewMusic, unsupportedReferences, ChartResourceKind.Audio);
        foreach (string channelName in EnumerateAudioChannelNames(document))
        {
            AddNormalizedComponentPath(files, channelName, unsupportedReferences, ChartResourceKind.Audio);
        }
        return [.. files.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> ReadBmsonBgaFiles(BmsonDocument document, ICollection<UnsupportedChartResourceReference> unsupportedReferences)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BmsonBgaHeader header in document?.Bga?.BgaHeader ?? [])
        {
            string normalized = NormalizeComponentPath(header?.Name, unsupportedReferences, ChartResourceKind.Unknown);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                ChartResourceKind kind = ChartResourcePathNormalizer.ClassifyPath(normalized);
                if (kind == ChartResourceKind.Image || kind == ChartResourceKind.Movie)
                {
                    files.Add(normalized);
                }
            }
        }
        return [.. files.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> EnumerateAudioChannelNames(BmsonDocument document)
    {
        return (document?.SoundChannels ?? []).Select(channel => channel?.Name)
            .Concat((document?.KeyChannels ?? []).Select(channel => channel?.Name))
            .Concat((document?.MineChannels ?? []).Select(channel => channel?.Name));
    }

    private static void AddNormalizedComponentPath(
        ISet<string> files,
        string filePath,
        ICollection<UnsupportedChartResourceReference> unsupportedReferences,
        ChartResourceKind kind)
    {
        string normalized = NormalizeComponentPath(filePath, unsupportedReferences, kind);
        if (files == null || string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }
        if (ChartResourcePathNormalizer.ClassifyPath(normalized) == ChartResourceKind.Audio)
        {
            files.Add(normalized);
        }
    }

    private static string NormalizeComponentPath(
        string filePath,
        ICollection<UnsupportedChartResourceReference> unsupportedReferences,
        ChartResourceKind kind)
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(filePath);
        if (result.IsValid)
        {
            return result.NormalizedPath;
        }
        if (result.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
        {
            unsupportedReferences?.Add(new UnsupportedChartResourceReference(
                kind == ChartResourceKind.Unknown ? ChartResourcePathNormalizer.ClassifyReferencePathExtension(filePath) : kind,
                filePath,
                result.Status));
        }
        return string.Empty;
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
        string safeSubartists = string.Join(",", (subartists ?? []).Where(item => !string.IsNullOrWhiteSpace(item)));
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

    private static string ComputeHash(string filePath, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (FileStream stream = LongPathFileSystem.OpenRead(filePath))
        {
            byte[] hash = algorithm.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
