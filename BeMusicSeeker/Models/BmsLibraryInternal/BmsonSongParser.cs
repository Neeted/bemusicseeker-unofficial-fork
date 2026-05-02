using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class BmsonSongParser
{
    public static LR2SongDBExtended.bmson_song Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        string fullPath = Path.GetFullPath(filePath);
        string json = File.ReadAllText(fullPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
        BmsonDocument root = BmsonJsonParser.Parse(json);
        DateTime updatedAt = File.GetLastWriteTimeUtc(fullPath);
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
        LR2SongDBExtended.bmson_song result = new LR2SongDBExtended.bmson_song
        {
            path = fullPath,
            folder = Path.GetDirectoryName(fullPath) ?? string.Empty,
            title = info.Title ?? string.Empty,
            subtitle = ComposeSubtitle(info.Subtitle, info.ChartName),
            artist = ComposeArtist(info.Artist, info.Subartists ?? Array.Empty<string>()),
            genre = info.Genre ?? string.Empty,
            level = info.Level.HasValue ? (double?)info.Level.Value : null,
            mode_hint = info.ModeHint ?? string.Empty,
            md5 = md5,
            sha256 = sha256,
            banner = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(info.BannerImage),
            backbmp = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(info.BackImage),
            stagefile = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(info.EyecatchImage),
            preview_music = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(info.PreviewMusic),
            updated_at = updatedAt
        };
        result.wav_files = ReadBmsonWavFiles(root, result.preview_music);
        result.bga_files = ReadBmsonBgaFiles(root);
        result.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(result.path, result.md5);
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

    private static List<string> ReadBmsonWavFiles(BmsonDocument document, string previewMusic)
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalizedComponentPath(files, previewMusic);
        foreach (string channelName in EnumerateAudioChannelNames(document))
        {
            AddNormalizedComponentPath(files, channelName);
        }
        return files.OrderBy((string item) => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ReadBmsonBgaFiles(BmsonDocument document)
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BmsonBgaHeader header in document?.Bga?.BgaHeader ?? Array.Empty<BmsonBgaHeader>())
        {
            string normalized = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(header?.Name);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                ChartResourceKind kind = ChartResourcePathNormalizer.ClassifyPath(normalized);
                if (kind == ChartResourceKind.Image || kind == ChartResourceKind.Movie)
                {
                    files.Add(normalized);
                }
            }
        }
        return files.OrderBy((string item) => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateAudioChannelNames(BmsonDocument document)
    {
        return (document?.SoundChannels ?? Array.Empty<BmsonSoundChannel>()).Select((BmsonSoundChannel channel) => channel?.Name)
            .Concat((document?.KeyChannels ?? Array.Empty<BmsonMineChannel>()).Select((BmsonMineChannel channel) => channel?.Name))
            .Concat((document?.MineChannels ?? Array.Empty<BmsonMineChannel>()).Select((BmsonMineChannel channel) => channel?.Name));
    }

    private static void AddNormalizedComponentPath(ISet<string> files, string filePath)
    {
        string normalized = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(filePath);
        if (files == null || string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }
        if (ChartResourcePathNormalizer.ClassifyPath(normalized) == ChartResourceKind.Audio)
        {
            files.Add(normalized);
        }
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
