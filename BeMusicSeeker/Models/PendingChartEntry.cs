using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

public enum PendingChartKind
{
    Bms,
    Bmson
}

public sealed class PendingChartEntry : BMSFile
{
    public static readonly string[] bmsonExtensions = new string[1] { ".bmson" };

    private string displayLevel = string.Empty;

    private string displayFolder = string.Empty;

    private string displayTitle = string.Empty;

    private string displayArtist = string.Empty;

    public PendingChartKind ChartKind { get; private set; }

    public LR2SongDBExtended.bmson_song BmsonSong { get; private set; }

    public bool IsBmsonChart => ChartKind == PendingChartKind.Bmson;

    public bool IsBmsChart => ChartKind == PendingChartKind.Bms;

    public override string Title => IsBmsonChart ? displayTitle : base.Title;

    public override string Artist => IsBmsonChart ? displayArtist : base.Artist;

    public override string Level => IsBmsonChart ? displayLevel : base.Level;

    public override string Folder => IsBmsonChart ? displayFolder : base.Folder;

    private PendingChartEntry()
    {
    }

    public static PendingChartEntry CreateFromFilePath(string filePath)
    {
        if (IsBmsonFilePath(filePath))
        {
            return CreateFromBmsonSong(BmsonSongParser.Parse(filePath));
        }
        return CreateFromBmsFile(BMSFile.CreateBMSFileFromFile(filePath));
    }

    public static PendingChartEntry CreateFromBmsFile(BMSFile source)
    {
        if (source is PendingChartEntry pending)
        {
            return pending;
        }
        if (source == null)
        {
            return null;
        }
        PendingChartEntry entry = new PendingChartEntry
        {
            ChartKind = PendingChartKind.Bms,
            path = source.path,
            type = source.type,
            mode = source.mode,
            folder = source.folder,
            parent = source.parent,
            level = source.level,
            difficulty = source.difficulty,
            tag = source.tag,
            notes = source.notes,
            instl_dst = source.instl_dst,
            warning = source.warning,
            status = source.status,
            HasZeroNoteMismatchWarning = source.HasZeroNoteMismatchWarning,
            IsHashDuplicated = source.IsHashDuplicated,
            WAVfiles = source.WAVfiles != null ? new HashSet<string>(source.WAVfiles, StringComparer.OrdinalIgnoreCase) : null,
            BGAfiles = source.BGAfiles != null ? new HashSet<string>(source.BGAfiles, StringComparer.OrdinalIgnoreCase) : null
        };
        entry.hash = source.hash;
        entry.sha256 = source.sha256;
        entry.title = source.title;
        entry.subtitle = source.subtitle;
        entry.artist = source.artist;
        entry.subartist = source.subartist;
        entry.genre = source.genre;
        entry.stagefile = source.stagefile;
        entry.banner = source.banner;
        entry.backbmp = source.backbmp;
        if (source.HasMaintenanceInfoHash(source.hash))
        {
            entry.SetMaintenanceInfo(source.maintenanceInfo, suppressPropertyChanged: true, registerEventHandlers: false);
        }
        else
        {
            entry.SetMaintenanceInfo(new BMSFileMaintenanceInfo(entry), suppressPropertyChanged: true, registerEventHandlers: false);
        }
        if (source.bmsScore != null)
        {
            entry.bmsScore = source.bmsScore;
        }
        return entry;
    }

    public static PendingChartEntry CreateFromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return null;
        }
        PendingChartEntry entry = new PendingChartEntry
        {
            ChartKind = PendingChartKind.Bmson,
            BmsonSong = song,
            path = song.path,
            instl_dst = null,
            status = BMSFileStatus.NONE,
            displayTitle = BmsonSongParser.ComposeDisplayTitle(song),
            displayArtist = song.artist ?? string.Empty,
            displayLevel = song.level?.ToString() ?? string.Empty,
            displayFolder = BmsonSongParser.ComposeDisplayFolder(song),
            tag = song.mode_hint ?? string.Empty,
            folder = song.folder,
            parent = null,
            type = 0
        };
        entry.hash = song.md5;
        entry.sha256 = song.sha256;
        entry.title = song.title;
        entry.subtitle = song.subtitle;
        entry.artist = song.artist;
        entry.genre = song.genre;
        entry.stagefile = song.stagefile;
        entry.banner = song.banner;
        entry.backbmp = song.backbmp;
        entry.mode = BmsonSongParser.ResolvePlaylistMode(song.mode_hint);
        entry.WAVfiles = new HashSet<string>(song.wav_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        entry.BGAfiles = new HashSet<string>(song.bga_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        entry.SetMaintenanceInfo(new BMSFileMaintenanceInfo(entry), suppressPropertyChanged: true, registerEventHandlers: false);
        return entry;
    }

    public static bool IsBmsonFilePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && bmsonExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSupportedChartFilePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }
        return BMSFile.bmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || bmsonExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsBmsonChartFile(BMSFile file)
    {
        return file is PendingChartEntry pending && pending.IsBmsonChart;
    }

    public static bool IsBmsChartFile(BMSFile file)
    {
        return !IsBmsonChartFile(file);
    }

    public static string GetPrimaryMd5(BMSFile file)
    {
        return file?.hash;
    }

    public static string GetSecondarySha256(BMSFile file)
    {
        return file?.sha256;
    }

    public static string GetPrimaryLookupHash(BMSFile file)
    {
        if (!string.IsNullOrWhiteSpace(file?.hash))
        {
            return file.hash;
        }
        if (!string.IsNullOrWhiteSpace(file?.sha256))
        {
            return file.sha256;
        }
        return null;
    }

    public static string GetPrimaryLookupHash(LR2SongDBExtended.bmson_song song)
    {
        if (!string.IsNullOrWhiteSpace(song?.md5))
        {
            return song.md5;
        }
        if (!string.IsNullOrWhiteSpace(song?.sha256))
        {
            return song.sha256;
        }
        return null;
    }

    public static IEnumerable<string> GetAllLookupHashes(BMSFile file)
    {
        if (!string.IsNullOrWhiteSpace(file?.hash))
        {
            yield return file.hash;
        }
        if (!string.IsNullOrWhiteSpace(file?.sha256))
        {
            yield return file.sha256;
        }
    }

    public static IEnumerable<string> GetLookupKeys(BMSFile file)
    {
        return GetAllLookupHashes(file);
    }
}
