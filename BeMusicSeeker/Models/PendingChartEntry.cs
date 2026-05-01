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

public enum PendingChartLookupHashKind
{
    None,
    Md5,
    Sha256
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

    public override LR2SongDBExtended.chart_info ChartInfo => IsBmsonChart
        ? BmsonSong?.ChartInfo ?? base.ChartInfo
        : base.ChartInfo;

    public override string Title
    {
        get
        {
            return IsBmsonChart ? displayTitle : base.Title;
        }
        protected set
        {
            if (IsBmsonChart)
            {
                SetDisplayValue(ref displayTitle, value, nameof(Title));
            }
            else
            {
                base.Title = value;
            }
        }
    }

    public override string Artist
    {
        get
        {
            return IsBmsonChart ? displayArtist : base.Artist;
        }
        protected set
        {
            if (IsBmsonChart)
            {
                SetDisplayValue(ref displayArtist, value, nameof(Artist));
            }
            else
            {
                base.Artist = value;
            }
        }
    }

    public override string Level
    {
        get
        {
            return IsBmsonChart ? displayLevel : base.Level;
        }
        set
        {
            if (IsBmsonChart)
            {
                SetDisplayValue(ref displayLevel, value, nameof(Level));
            }
            else
            {
                base.Level = value;
            }
        }
    }

    public override string Folder
    {
        get
        {
            return IsBmsonChart ? displayFolder : base.Folder;
        }
        set
        {
            if (IsBmsonChart)
            {
                SetDisplayValue(ref displayFolder, value, nameof(Folder));
            }
            else
            {
                base.Folder = value;
            }
        }
    }

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
            InstallDestinationTitle = source.InstallDestinationTitle,
            InstallDestinationArtist = source.InstallDestinationArtist,
            InstallDestinationSuggestions = source.InstallDestinationSuggestions?.ToArray() ?? Array.Empty<string>(),
            status = source.status,
            HasZeroNoteMismatchWarning = source.HasZeroNoteMismatchWarning,
            HasLowConfidenceInstallWarning = source.HasLowConfidenceInstallWarning,
            IsInstallDestinationSuggestionPopupOpen = false,
            IsHashDuplicated = source.IsHashDuplicated,
            WAVfiles = source.WAVfiles != null ? new HashSet<string>(source.WAVfiles, StringComparer.OrdinalIgnoreCase) : null,
            BGAfiles = source.BGAfiles != null ? new HashSet<string>(source.BGAfiles, StringComparer.OrdinalIgnoreCase) : null
        };
        entry.CopyStructuredWarningsFrom(source);
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
        entry.SetChartInfo(source.ChartInfo);
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
            instl_dst = null,
            status = BMSFileStatus.NONE
        };
        entry.UpdateFromBmsonSong(song);
        return entry;
    }

    public void UpdateFromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            throw new ArgumentNullException(nameof(song));
        }
        ChartKind = PendingChartKind.Bmson;
        BmsonSong = song;
        path = song.path;
        instl_dst = null;
        InstallDestinationTitle = string.Empty;
        InstallDestinationArtist = string.Empty;
        InstallDestinationSuggestions = Array.Empty<string>();
        status = BMSFileStatus.NONE;
        tag = string.Empty;
        HasLowConfidenceInstallWarning = false;
        IsInstallDestinationSuggestionPopupOpen = false;
        folder = song.folder;
        parent = null;
        type = 0;
        hash = song.md5;
        sha256 = song.sha256;
        SetChartInfo(song.ChartInfo);
        BMSFileMaintenanceInfo nextMaintenanceInfo = song.MaintenanceInfo ?? maintenanceInfo;
        if (nextMaintenanceInfo == null || !string.Equals(nextMaintenanceInfo.hash, hash, StringComparison.OrdinalIgnoreCase))
        {
            nextMaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(path, hash);
        }
        else
        {
            nextMaintenanceInfo.NormalizeForBmson(path, hash);
        }
        SetMaintenanceInfo(nextMaintenanceInfo, suppressPropertyChanged: true, registerEventHandlers: false);
        song.MaintenanceInfo = nextMaintenanceInfo;
        title = song.title;
        subtitle = song.subtitle;
        artist = song.artist;
        genre = song.genre;
        stagefile = song.stagefile;
        banner = song.banner;
        backbmp = song.backbmp;
        mode = BmsonSongParser.ResolvePlaylistMode(song.mode_hint);
        WAVfiles = new HashSet<string>(song.wav_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BGAfiles = new HashSet<string>(song.bga_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        SetDisplayValue(ref displayTitle, BmsonSongParser.ComposeDisplayTitle(song), nameof(Title));
        SetDisplayValue(ref displayArtist, song.artist ?? string.Empty, nameof(Artist));
        SetDisplayValue(ref displayLevel, song.level?.ToString() ?? string.Empty, nameof(Level));
        SetDisplayValue(ref displayFolder, BmsonSongParser.ComposeDisplayFolder(song), nameof(Folder));
    }

    internal void UpdateBmsonResourceReferences(LR2SongDBExtended.bmson_song parsed)
    {
        if (parsed == null || !IsBmsonChart)
        {
            return;
        }
        LR2SongDBExtended.bmson_song target = BmsonSong ?? parsed;
        target.stagefile = parsed.stagefile;
        target.banner = parsed.banner;
        target.backbmp = parsed.backbmp;
        target.preview_music = parsed.preview_music;
        target.wav_files = parsed.wav_files ?? new List<string>();
        target.bga_files = parsed.bga_files ?? new List<string>();
        if (target.MaintenanceInfo == null)
        {
            target.MaintenanceInfo = maintenanceInfo;
        }
        stagefile = target.stagefile;
        banner = target.banner;
        backbmp = target.backbmp;
        WAVfiles = new HashSet<string>(target.wav_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BGAfiles = new HashSet<string>(target.bga_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    internal void ReplaceBmsonSongReferenceAfterInstall(LR2SongDBExtended.bmson_song installedSong)
    {
        if (installedSong == null || !IsBmsonChart)
        {
            return;
        }
        BmsonSong = installedSong;
        path = installedSong.path;
        folder = installedSong.folder;
        hash = installedSong.md5;
        sha256 = installedSong.sha256;
        SetChartInfo(installedSong.ChartInfo);
        BMSFileMaintenanceInfo nextMaintenanceInfo = installedSong.MaintenanceInfo ?? maintenanceInfo ?? BMSFileMaintenanceInfo.CreateForBmson(path, hash);
        nextMaintenanceInfo.NormalizeForBmson(path, hash);
        SetMaintenanceInfo(nextMaintenanceInfo, suppressPropertyChanged: true, registerEventHandlers: false);
        installedSong.MaintenanceInfo = nextMaintenanceInfo;
        WAVfiles = new HashSet<string>(installedSong.wav_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BGAfiles = new HashSet<string>(installedSong.bga_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
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
        return file != null && !IsBmsonChartFile(file);
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

    public static PendingChartLookupHashKind GetPrimaryLookupHashKind(BMSFile file)
    {
        if (!string.IsNullOrWhiteSpace(file?.hash))
        {
            return PendingChartLookupHashKind.Md5;
        }
        if (!string.IsNullOrWhiteSpace(file?.sha256))
        {
            return PendingChartLookupHashKind.Sha256;
        }
        return PendingChartLookupHashKind.None;
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

    public static PendingChartLookupHashKind GetPrimaryLookupHashKind(LR2SongDBExtended.bmson_song song)
    {
        if (!string.IsNullOrWhiteSpace(song?.md5))
        {
            return PendingChartLookupHashKind.Md5;
        }
        if (!string.IsNullOrWhiteSpace(song?.sha256))
        {
            return PendingChartLookupHashKind.Sha256;
        }
        return PendingChartLookupHashKind.None;
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

    private void SetDisplayValue(ref string currentValue, string newValue, string propertyName)
    {
        string normalized = newValue ?? string.Empty;
        if (!string.Equals(currentValue, normalized, StringComparison.Ordinal))
        {
            currentValue = normalized;
            RaisePropertyChanged(propertyName);
        }
    }
}
