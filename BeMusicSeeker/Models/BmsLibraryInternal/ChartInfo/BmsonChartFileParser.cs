using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>捕捉済み入力からbmsonの基本値と不変リソース結果を直接作成します。</summary>
internal static class BmsonChartFileParser
{
    /// <summary>同じ捕捉内容のハッシュと時刻を保持し、保存行を経由せずに基本値を返します。原題の省略・nullは空として保持し、合成表示で補完しません。</summary>
    internal static ChartFile ParseSnapshot(ChartFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(snapshot.Bytes);
        BmsonDocument root = BmsonJsonParser.Parse(json);
        BmsonInfo info = root?.Info ?? new BmsonInfo();
        var references = new List<ChartResourceReference>();
        string banner = ExtractComponentPath(info.BannerImage, references, ChartResourceKind.Image, ChartResourceUsage.Banner);
        string backbmp = ExtractComponentPath(info.BackImage, references, ChartResourceKind.Image, ChartResourceUsage.Backbmp);
        string stagefile = ExtractComponentPath(info.EyecatchImage, references, ChartResourceKind.Image, ChartResourceUsage.Stagefile);
        string preview = string.IsNullOrWhiteSpace(info.PreviewMusic) ? string.Empty : ExtractComponentPath(info.PreviewMusic, references, ChartResourceKind.Audio);
        foreach ((string Name, bool HasNotes) channel in EnumerateAudioChannels(root))
        {
            ExtractComponentPath(channel.Name, references, ChartResourceKind.Audio, isUsed: channel.HasNotes);
        }
        var usedBgaIds = new HashSet<int>((root?.Bga?.BgaEvents ?? []).Concat(root?.Bga?.LayerEvents ?? [])
            .Concat(root?.Bga?.PoorEvents ?? []).Where(note => note?.Id.HasValue == true).Select(note => note.Id.Value));
        foreach (BmsonBgaHeader header in root?.Bga?.BgaHeader ?? [])
        {
            if (header == null)
            {
                continue;
            }

            ExtractComponentPath(header.Name, references, ChartResourceKind.Unknown, isUsed: header.Id.HasValue && usedBgaIds.Contains(header.Id.Value));
        }
        string subtitle = ComposeSubtitle(info.Subtitle, info.ChartName);
        string title = string.IsNullOrWhiteSpace(subtitle) ? info.Title ?? string.Empty
            : string.IsNullOrWhiteSpace(info.Title) ? subtitle : info.Title + " " + subtitle;
        return new ChartFile(ChartFileKind.Bmson, snapshot.Path, snapshot.Md5, snapshot.Sha256,
            title, info.Title ?? string.Empty, ComposeArtist(info.Artist, info.Subartists), info.Genre,
            Path.GetFileName(Path.GetDirectoryName(snapshot.Path)), string.Empty,
            info.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, info.Level,
            ResolvePlaylistMode(info.ModeHint), null,
            subtitle: subtitle, resources: references.ToImmutableList(),
            stagefile: stagefile, backbmp: backbmp, banner: banner)
        {
            RawSubtitle = info.Subtitle ?? string.Empty,
            RawArtist = info.Artist ?? string.Empty,
            Subartist = string.Join(",", (info.Subartists ?? []).Where(item => !string.IsNullOrWhiteSpace(item))),
            ChartName = info.ChartName ?? string.Empty,
            ModeHint = info.ModeHint ?? string.Empty,
            PreviewMusic = preview,
            LastWriteTimeUtc = snapshot.LastWriteTimeUtc
        };
    }
    private static IEnumerable<(string Name, bool HasNotes)> EnumerateAudioChannels(BmsonDocument document)
    {
        return (document?.SoundChannels ?? []).Where(channel => channel != null).Select(channel => (channel.Name, channel.Notes?.Length > 0))
            .Concat((document?.KeyChannels ?? []).Where(channel => channel != null).Select(channel => (channel.Name, channel.Notes?.Length > 0)))
            .Concat((document?.MineChannels ?? []).Where(channel => channel != null).Select(channel => (channel.Name, channel.Notes?.Length > 0)));
    }

    /// <summary>原文と解析状態を一回の入口で確定します。使用される通常参照はnullも照合不能として保持し、任意画像の省略と音声分類は維持します。</summary>
    private static string ExtractComponentPath(string path, ICollection<ChartResourceReference> references,
        ChartResourceKind expectedKind, ChartResourceUsage usage = ChartResourceUsage.Normal, bool isUsed = true)
    {
        if (path == null && (usage != ChartResourceUsage.Normal || !isUsed))
        {
            return string.Empty;
        }
        var reference = ChartResourceReference.Parse(path, ChartResourceKind.Unknown, usage);
        // bmsonの音声候補は音声拡張子だけを検索対象とする。非対応理由の種別は定義用途を維持する。
        ChartResourceKind kind = usage != ChartResourceUsage.Normal || reference.Status != ChartResourcePathNormalizationStatus.Valid
            ? expectedKind == ChartResourceKind.Unknown ? reference.Kind : expectedKind
            : (expectedKind == ChartResourceKind.Audio && reference.Kind != ChartResourceKind.Audio)
                || (expectedKind == ChartResourceKind.Unknown && reference.Kind == ChartResourceKind.Audio)
                    ? ChartResourceKind.Unknown : reference.Kind;
        reference = new ChartResourceReference(kind, usage, reference.RawPath, reference.NormalizedPath, reference.LookupKey, reference.Status, isUsed);
        references.Add(reference);
        return reference.Status == ChartResourcePathNormalizationStatus.Valid ? reference.NormalizedPath : string.Empty;
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

}
