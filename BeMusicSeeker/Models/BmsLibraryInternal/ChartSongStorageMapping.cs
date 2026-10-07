using System;
using System.Globalization;
using System.IO;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>既存のDB境界でだけ、共通値を保存行へ変換します。</summary>
internal static class ChartSongStorageMapping
{
    /// <summary>song行と別表で読んだSHA-256から基本値を一度だけ生成します。生メタデータを表示合成から復元しません。</summary>
    internal static ChartFile FromBmsRow(LR2SongDB.song row, string sha256 = null)
    {
        if (row == null)
        {
            return null;
        }

        return new ChartFile(ChartFileKind.Bms, row.path, row.hash, sha256,
            string.IsNullOrWhiteSpace(row.subtitle) ? row.title : row.title + " " + row.subtitle,
            row.title, string.IsNullOrWhiteSpace(row.subartist) ? row.artist : row.artist + " " + row.subartist,
            row.genre, GetDisplayFolder(row.path), row.StoredTag, row.level?.ToString(CultureInfo.InvariantCulture), row.level, row.mode, null, row.subtitle)
        {
            Warnings = Lr2CompatibilityWarningProjection.BuildWarnings(new ResourceHealthMaintenanceSnapshot
            { Lr2WarningFlags = (int)Lr2CompatibilityEvaluator.EvaluateChartPath(row.path).WarningFlags }),
            Stagefile = row.stagefile,
            Banner = row.banner,
            Backbmp = row.backbmp,
            RawSubtitle = row.subtitle,
            RawArtist = row.artist,
            Subartist = row.subartist,
            Favorite = row.favorite,
            AddDate = row.adddate,
            Txt = row.txt,
            Date = row.date,
            Difficulty = row.difficulty,
            Judge = row.judge
        };
    }

    /// <summary>bmson行から基本値を一度だけ生成します。元chart_nameはファイル読取り時だけ得られます。</summary>
    internal static ChartFile FromBmsonRow(LR2SongDBExtended.bmson_song row)
    {
        if (row == null)
        {
            return null;
        }

        return new ChartFile(ChartFileKind.Bmson, row.path, row.md5, row.sha256,
            string.IsNullOrWhiteSpace(row.title) ? row.subtitle : string.IsNullOrWhiteSpace(row.subtitle) ? row.title : row.title + " " + row.subtitle, row.title, row.artist, row.genre,
            GetDisplayFolder(row.path), string.Empty, row.level?.ToString(CultureInfo.InvariantCulture), row.level,
            BmsonChartFileParser.ResolvePlaylistMode(row.mode_hint), null, row.subtitle)
        {
            RawArtist = row.artist,
            ModeHint = row.mode_hint,
            PreviewMusic = row.preview_music,
            LastWriteTimeUtc = row.updated_at,
            Stagefile = row.stagefile,
            Banner = row.banner,
            Backbmp = row.backbmp
        };
    }

    private static string GetDisplayFolder(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;

    /// <summary>共通基本値と詳細値からLR2の生成列を書き込む行を作成します。</summary>
    internal static LR2SongDB.song ToBmsRow(ChartFile chart)
    {
        if (chart == null || chart.Kind != ChartFileKind.Bms)
        {
            return null;
        }
        var row = new LR2SongDB.song
        {
            path = chart.Path,
            hash = chart.Md5,
            title = chart.RawTitle,
            subtitle = chart.RawSubtitle,
            artist = chart.RawArtist,
            subartist = chart.Subartist,
            genre = chart.Genre,
            tag = chart.Tag,
            favorite = chart.Favorite,
            adddate = chart.AddDate,
            txt = chart.Txt,
            level = chart.Level.HasValue ? (int?)chart.Level.Value : null,
            mode = chart.Mode,
            difficulty = chart.Difficulty,
            judge = chart.Judge,
            banner = chart.Banner,
            backbmp = chart.Backbmp,
            stagefile = chart.Stagefile,
            date = chart.Date ?? (chart.LastWriteTimeUtc == default
                ? null : Lr2SongRowEnricher.ToLr2UnixSeconds(chart.LastWriteTimeUtc))
        };
        row.sha256 = chart.Sha256;
        if (chart.ChartInfo != null)
        {
            Lr2SongRowEnricher.EnrichFromChartInfo(row, chart.ChartInfo);
        }
        Lr2SongRowEnricher.EnrichGeneratedSong(row);
        return row;
    }

    /// <summary>共通値からbmsonの保存列を作成します。元chart_nameは合成字幕から復元しません。</summary>
    internal static LR2SongDBExtended.bmson_song ToBmsonRow(ChartFile chart) =>
        chart == null || chart.Kind != ChartFileKind.Bmson ? null : new()
        {
            path = chart.Path,
            folder = chart.Directory ?? string.Empty,
            md5 = chart.Md5,
            sha256 = chart.Sha256,
            title = chart.RawTitle,
            subtitle = chart.Subtitle,
            artist = chart.Artist,
            genre = chart.Genre,
            level = chart.Level,
            mode_hint = chart.ModeHint,
            preview_music = chart.PreviewMusic,
            banner = chart.Banner,
            backbmp = chart.Backbmp,
            stagefile = chart.Stagefile,
            updated_at = chart.LastWriteTimeUtc
        };
    internal static LR2SongDB.song FromRawSongValues(string[] values)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }
        var file = new LR2SongDB.song
        {
            hash = NormalizeMd5HashFromDb(GetRawValue(values, 0)),
            title = GetRawValue(values, 1),
            subtitle = GetRawValue(values, 2),
            artist = GetRawValue(values, 3),
            subartist = GetRawValue(values, 4),
            genre = GetRawValue(values, 5),
            tag = GetRawValue(values, 6),
            path = GetRawValue(values, 7),
            type = ParseNullableIntFromDb(GetRawValue(values, 8)),
            folder = GetRawValue(values, 9),
            stagefile = GetRawValue(values, 10),
            banner = GetRawValue(values, 11),
            backbmp = GetRawValue(values, 12),
            parent = GetRawValue(values, 13),
            level = ParseNullableIntFromDb(GetRawValue(values, 14)),
            difficulty = ParseNullableIntFromDb(GetRawValue(values, 15)),
            maxbpm = ParseNullableIntFromDb(GetRawValue(values, 16)),
            minbpm = ParseNullableIntFromDb(GetRawValue(values, 17)),
            mode = ParseNullableIntFromDb(GetRawValue(values, 18)),
            judge = ParseNullableIntFromDb(GetRawValue(values, 19)),
            longnote = ParseNullableIntFromDb(GetRawValue(values, 20)),
            bga = ParseNullableIntFromDb(GetRawValue(values, 21)),
            random = ParseNullableIntFromDb(GetRawValue(values, 22)),
            date = ParseNullableIntFromDb(GetRawValue(values, 23)),
            favorite = ParseNullableIntFromDb(GetRawValue(values, 24)),
            txt = ParseNullableIntFromDb(GetRawValue(values, 25)),
            karinotes = ParseNullableIntFromDb(GetRawValue(values, 26)),
            adddate = ParseNullableIntFromDb(GetRawValue(values, 27)),
            exlevel = ParseNullableIntFromDb(GetRawValue(values, 28))
        };
        return file;
    }

    private static string GetRawValue(string[] values, int index)
    {
        return index >= 0 && index < values.Length ? values[index] : null;
    }

    private static int? ParseNullableIntFromDb(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static string NormalizeMd5HashFromDb(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
        {
            return null;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return null;
            }
        }
        return value.ToLowerInvariant();
    }

}
