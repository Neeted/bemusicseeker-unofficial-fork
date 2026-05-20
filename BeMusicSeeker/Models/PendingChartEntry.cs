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
    public static readonly string[] bmsonExtensions = [".bmson"];

    private string displayLevel = string.Empty;

    private string displayFolder = string.Empty;

    private string displayTitle = string.Empty;

    private string displayArtist = string.Empty;

    public PendingChartKind ChartKind { get; private set; }

    public LR2SongDBExtended.bmson_song BmsonSong { get; private set; }

    public bool IsBmsonChart => ChartKind == PendingChartKind.Bmson;

    public bool IsBmsChart => ChartKind == PendingChartKind.Bms;

    private static bool IsMeaningfulResourceMaintenanceInfo(BMSFileMaintenanceInfo info)
    {
        if (info == null)
        {
            return false;
        }
        return info.IsInformationChecked()
            || info.wav_files_defined.HasValue
            || info.wav_files_existing.HasValue
            || info.bga_files_defined.HasValue
            || info.bga_files_existing.HasValue
            || info.movie_files_defined.HasValue
            || info.movie_files_existing.HasValue
            || info.is_stagefile_defined.HasValue
            || info.is_stagefile_existing.HasValue
            || info.is_banner_defined.HasValue
            || info.is_banner_existing.HasValue
            || info.is_backbmp_defined.HasValue
            || info.is_backbmp_existing.HasValue
            || info.is_files_warning_ignored;
    }

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
        var entry = new PendingChartEntry
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
            InstallDestinationSuggestions = source.InstallDestinationSuggestions?.ToArray() ?? [],
            status = source.status,
            IsInstallDestinationSuggestionPopupOpen = false,
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
        if (source.HasValidMaintenanceInfoSnapshot && source.HasMaintenanceInfoHash(source.hash))
        {
            entry.SetMaintenanceInfo(source.TryGetMaintenanceInfoWithoutCreating(), suppressPropertyChanged: true, registerEventHandlers: false, source.MaintenanceInfoOrigin);
        }
        else
        {
            entry.SetMaintenanceInfo(new BMSFileMaintenanceInfo(entry), suppressPropertyChanged: true, registerEventHandlers: false, MaintenanceInfoOrigin.Placeholder);
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
        var entry = new PendingChartEntry
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
        ChartKind = PendingChartKind.Bmson;
        BmsonSong = song ?? throw new ArgumentNullException(nameof(song));
        path = song.path;
        instl_dst = null;
        InstallDestinationTitle = string.Empty;
        InstallDestinationArtist = string.Empty;
        InstallDestinationSuggestions = [];
        status = BMSFileStatus.NONE;
        tag = string.Empty;
        IsInstallDestinationSuggestionPopupOpen = false;
        folder = song.folder;
        parent = null;
        type = 0;
        hash = song.md5;
        sha256 = song.sha256;
        SetChartInfo(song.ChartInfo);
        BMSFileMaintenanceInfo nextMaintenanceInfo = song.MaintenanceInfo;
        MaintenanceInfoOrigin origin = MaintenanceInfoOrigin.DbHydrated;
        if (nextMaintenanceInfo == null
            || !string.Equals(nextMaintenanceInfo.hash, hash, StringComparison.OrdinalIgnoreCase)
            || !IsMeaningfulResourceMaintenanceInfo(nextMaintenanceInfo))
        {
            nextMaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(path, hash);
            origin = MaintenanceInfoOrigin.Placeholder;
        }
        else
        {
            nextMaintenanceInfo.NormalizeForBmson(path, hash);
        }
        SetMaintenanceInfo(nextMaintenanceInfo, suppressPropertyChanged: true, registerEventHandlers: false, origin);
        if (origin != MaintenanceInfoOrigin.Placeholder)
        {
            song.MaintenanceInfo = nextMaintenanceInfo;
        }
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
        ClearComponentFileCache();
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
        target.wav_files = parsed.wav_files ?? [];
        target.bga_files = parsed.bga_files ?? [];
        target.HasFreshResourceReferences = parsed.HasFreshResourceReferences;
        target.MaintenanceInfo ??= maintenanceInfo;
        stagefile = target.stagefile;
        banner = target.banner;
        backbmp = target.backbmp;
        WAVfiles = new HashSet<string>(target.wav_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BGAfiles = new HashSet<string>(target.bga_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        ClearComponentFileCache();
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
        bool installedMaintenanceValid = IsMeaningfulResourceMaintenanceInfo(installedSong.MaintenanceInfo);
        MaintenanceInfoOrigin origin = installedMaintenanceValid
            ? MaintenanceInfoOrigin.DbHydrated
            : MaintenanceInfoOrigin;
        BMSFileMaintenanceInfo nextMaintenanceInfo = installedMaintenanceValid
            ? installedSong.MaintenanceInfo
            : null
            ?? (HasValidMaintenanceInfoSnapshot ? TryGetMaintenanceInfoWithoutCreating() : null)
            ?? BMSFileMaintenanceInfo.CreateForBmson(path, hash);
        if (!installedMaintenanceValid && !HasValidMaintenanceInfoSnapshot)
        {
            origin = MaintenanceInfoOrigin.Placeholder;
        }
        nextMaintenanceInfo.NormalizeForBmson(path, hash);
        SetMaintenanceInfo(nextMaintenanceInfo, suppressPropertyChanged: true, registerEventHandlers: false, origin);
        if (origin != MaintenanceInfoOrigin.Placeholder)
        {
            installedSong.MaintenanceInfo = nextMaintenanceInfo;
        }
        WAVfiles = new HashSet<string>(installedSong.wav_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BGAfiles = new HashSet<string>(installedSong.bga_files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        ClearComponentFileCache();
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
