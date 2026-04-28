using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// main DataGrid の行オブジェクトを、通常譜面行と playlist lightweight row の両方に対して解決します。
/// </summary>
internal static class GridRowResolver
{
    private static readonly Regex Sha256HashRegex = new Regex("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// playlist 詳細表示用 row かどうかを返します。
    /// </summary>
    internal static bool IsPlaylistRow(object row)
    {
        return row is PlaylistDetailRow;
    }

    /// <summary>
    /// 行から playlist エントリを取得します。
    /// </summary>
    internal static BMSTableEntry GetPlaylistEntry(object row)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.Entry;
        }
        return null;
    }

    /// <summary>
    /// 行から実体 BMS 譜面を取得します。
    /// bmson は chart target API で扱い、この互換 API では返しません。
    /// </summary>
    internal static BMSFile GetRealBmsFile(object row)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.RealFile;
        }
        if (row is LibraryChartRow libraryChartRow)
        {
            return libraryChartRow.BmsFile;
        }
        return row as BMSFile;
    }

    /// <summary>
    /// 行から BMS 互換操作対象を取得します。
    /// bmson owned row は <see cref="TryGetOperationChartTarget(object, ChartOperationSourceScope, out ChartOperationTarget)"/> を使ってください。
    /// </summary>
    internal static BMSFile GetOperationBmsFile(object row)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.RealFile;
        }
        if (row is LibraryChartRow libraryChartRow)
        {
            return libraryChartRow.BmsFile;
        }
        return row as BMSFile;
    }

    internal static bool TryGetChartRef(object row, out OwnedChartRef chart)
    {
        chart = null;
        switch (row)
        {
            case PlaylistDetailRow playlistDetailRow:
                return TryCreatePlaylistChartRef(playlistDetailRow, out chart);
            case PlaylistDetailSourceRow playlistSourceRow:
                return TryCreatePlaylistChartRef(playlistSourceRow, out chart);
            case LibraryChartRow libraryChartRow:
                chart = libraryChartRow.Chart;
                return chart != null;
            case BMSFile bmsFile:
                chart = CreateChartRef(bmsFile);
                return chart != null;
            default:
                return false;
        }
    }

    internal static bool TryGetOwnedChartRef(object row, out OwnedChartRef chart)
    {
        return TryGetChartRef(row, out chart);
    }

    internal static bool TryGetOperationChartTarget(object row, out ChartOperationTarget target)
    {
        return TryGetOperationChartTarget(row, ChartOperationSourceScope.Library, out target);
    }

    internal static bool TryGetOperationChartTarget(object row, bool isPendingSection, out ChartOperationTarget target)
    {
        return TryGetOperationChartTarget(row, isPendingSection ? ChartOperationSourceScope.PendingPackage : ChartOperationSourceScope.Library, out target);
    }

    internal static bool TryGetOperationChartTarget(object row, ChartOperationSourceScope sourceScope, out ChartOperationTarget target)
    {
        return TryGetChartOperationTarget(row, sourceScope, out target);
    }

    internal static bool TryGetChartOperationTarget(object row, out ChartOperationTarget target)
    {
        return TryGetChartOperationTarget(row, ChartOperationSourceScope.Library, out target);
    }

    internal static bool TryGetChartOperationTarget(object row, bool isPendingSection, out ChartOperationTarget target)
    {
        return TryGetChartOperationTarget(row, isPendingSection ? ChartOperationSourceScope.PendingPackage : ChartOperationSourceScope.Library, out target);
    }

    internal static bool TryGetChartOperationTarget(object row, ChartOperationSourceScope sourceScope, out ChartOperationTarget target)
    {
        target = null;
        if (!TryGetChartRef(row, out OwnedChartRef chart))
        {
            return false;
        }
        BMSTableEntry playlistEntry = GetPlaylistEntry(row);
        bool isPlaylistRow = IsPlaylistRow(row) || row is PlaylistDetailSourceRow;
        bool isOwned = row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.IsOwned,
            PlaylistDetailSourceRow playlistSourceRow => playlistSourceRow.IsOwned,
            LibraryChartRow => sourceScope != ChartOperationSourceScope.PendingPackage,
            _ => !string.IsNullOrWhiteSpace(chart.Path)
        };
        if (!isPlaylistRow && sourceScope == ChartOperationSourceScope.PendingPackage)
        {
            isOwned = false;
        }
        bool isPlaylistMissing = isPlaylistRow && !isOwned;
        if (isPlaylistRow)
        {
            sourceScope = isPlaylistMissing ? ChartOperationSourceScope.PlaylistMissing : ChartOperationSourceScope.PlaylistOwned;
        }
        bool isPending = sourceScope == ChartOperationSourceScope.PendingPackage;
        ChartOperationCapabilities capabilities = BuildCapabilities(chart, playlistEntry, sourceScope, isPlaylistRow, isOwned, isPlaylistMissing);
        target = new ChartOperationTarget(chart, playlistEntry, sourceScope, isOwned, isPending, isPlaylistMissing, capabilities);
        return true;
    }

    internal static bool IsBmsonChartRow(object row)
    {
        return TryGetChartRef(row, out OwnedChartRef chart) && chart.Kind == OwnedChartKind.Bmson;
    }

    internal static bool IsBmsonContextRow(object row)
    {
        return IsBmsonChartRow(row);
    }

    /// <summary>
    /// 行の URL1 を取得します。
    /// </summary>
    internal static Uri GetUrl(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.Url,
            _ => null
        };
    }

    /// <summary>
    /// 行の URL2 を取得します。
    /// </summary>
    internal static Uri GetUrlDiff(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.Url_diff,
            _ => null
        };
    }

    /// <summary>
    /// 行の LR2BMSID を取得します。
    /// </summary>
    internal static string GetLr2BmsId(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.lr2_bmsid,
            _ => null
        };
    }

    /// <summary>
    /// 行の差分名を取得します。
    /// </summary>
    internal static string GetNameDiff(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.name_diff,
            _ => null
        };
    }

    /// <summary>
    /// 行のハッシュを取得します。
    /// </summary>
    internal static string GetHash(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.hash,
            LibraryChartRow libraryChartRow => libraryChartRow.hash,
            BMSFile bmsFile => bmsFile.hash,
            _ => null
        };
    }

    /// <summary>
    /// 行の SHA256 ハッシュを取得します。
    /// </summary>
    internal static string GetSha256(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.sha256,
            LibraryChartRow libraryChartRow => libraryChartRow.sha256,
            BMSFile bmsFile => bmsFile.sha256,
            _ => null
        };
    }

    /// <summary>
    /// Mocha / MinIR など SHA256 ベースの外部 repository 連携に使うハッシュを取得します。
    /// 表示用 row に materialize 済みの値を優先し、通常 BMS 行では chart_info の SHA256 も fallback にします。
    /// </summary>
    internal static string GetRepositorySha256(object row)
    {
        string sha256 = TryGetChartRef(row, out OwnedChartRef chart)
            ? FirstNonEmpty(chart.Sha256, chart.ChartInfo?.sha256)
            : row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.sha256,
            LibraryChartRow libraryChartRow => FirstNonEmpty(libraryChartRow.sha256, libraryChartRow.ChartInfo?.sha256),
            BMSFile bmsFile => FirstNonEmpty(bmsFile.sha256, bmsFile.ChartInfo?.sha256),
            _ => null
        };
        return IsValidSha256(sha256) ? sha256.ToLowerInvariant() : null;
    }

    private static bool TryCreatePlaylistChartRef(PlaylistDetailRow row, out OwnedChartRef chart)
    {
        chart = null;
        if (row == null)
        {
            return false;
        }
        if (row.RealFile != null)
        {
            chart = CreateChartRef(row.RealFile);
            return chart != null;
        }
        if (row.ResolvedBmson != null)
        {
            chart = CreateChartRef(row.ResolvedBmson);
            return chart != null;
        }
        chart = new OwnedChartRef(
            OwnedChartKind.Bms,
            row.path,
            row.hash,
            row.sha256,
            row.Title,
            row.Artist,
            row.Entry?.level,
            row.mode,
            null,
            null,
            null);
        return true;
    }

    private static bool TryCreatePlaylistChartRef(PlaylistDetailSourceRow row, out OwnedChartRef chart)
    {
        chart = null;
        if (row == null)
        {
            return false;
        }
        if (row.RealFile != null)
        {
            chart = CreateChartRef(row.RealFile);
            return chart != null;
        }
        if (row.ResolvedBmson != null)
        {
            chart = CreateChartRef(row.ResolvedBmson);
            return chart != null;
        }
        chart = new OwnedChartRef(
            OwnedChartKind.Bms,
            row.path,
            row.hash,
            row.sha256,
            row.Title,
            row.Artist,
            row.Entry?.level,
            row.mode,
            row.ChartInfo,
            null,
            null);
        return true;
    }

    private static OwnedChartRef CreateChartRef(BMSFile file)
    {
        if (file == null)
        {
            return null;
        }
        PendingChartEntry pending = file as PendingChartEntry;
        if (pending?.IsBmsonChart == true && pending.BmsonSong != null)
        {
            LR2SongDBExtended.bmson_song song = pending.BmsonSong;
            return new OwnedChartRef(
                OwnedChartKind.Bmson,
                pending.path,
                pending.hash,
                pending.sha256,
                pending.Title,
                pending.Artist,
                pending.level ?? song.level,
                pending.mode ?? BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
                pending.ChartInfo,
                file,
                song);
        }
        return new OwnedChartRef(
            OwnedChartKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file.Title,
            file.Artist,
            ParseNullableDouble(file.Level),
            file.mode,
            file.ChartInfo,
            file,
            null);
    }

    private static OwnedChartRef CreateChartRef(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return null;
        }
        return new OwnedChartRef(
            OwnedChartKind.Bmson,
            song.path,
            song.md5,
            song.sha256,
            BmsonSongParser.ComposeDisplayTitle(song),
            song.artist,
            song.level,
            BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
            song.ChartInfo,
            null,
            song);
    }

    private static ChartOperationCapabilities BuildCapabilities(
        OwnedChartRef chart,
        BMSTableEntry playlistEntry,
        ChartOperationSourceScope sourceScope,
        bool isPlaylistRow,
        bool isOwned,
        bool isPlaylistMissing)
    {
        ChartOperationCapabilities capabilities = ChartOperationCapabilities.None;
        bool hasPath = !string.IsNullOrWhiteSpace(chart.Path);
        bool hasMd5 = !string.IsNullOrWhiteSpace(chart.Md5);
        bool isBms = chart.Kind == OwnedChartKind.Bms;
        if (hasPath && !isPlaylistMissing)
        {
            capabilities |= ChartOperationCapabilities.OpenFile | ChartOperationCapabilities.OpenFolder;
        }
        if (IsValidSha256(chart.Sha256) || IsValidSha256(chart.ChartInfo?.sha256))
        {
            capabilities |= ChartOperationCapabilities.OpenRepositoryBySha256;
        }
        if (isPlaylistRow && (playlistEntry?.EffectiveUrl != null || playlistEntry?.EffectiveUrlDiff != null))
        {
            capabilities |= ChartOperationCapabilities.OpenPlaylistUrls;
        }
        if (hasPath && !isPlaylistMissing)
        {
            capabilities |= ChartOperationCapabilities.RunResourceHealthCheck;
        }
        if (isBms)
        {
            if (hasMd5 || !string.IsNullOrWhiteSpace(playlistEntry?.lr2_bmsid))
            {
                capabilities |= ChartOperationCapabilities.UseLr2Ir;
            }
            if (hasMd5)
            {
                capabilities |= ChartOperationCapabilities.UseScoreViewer | ChartOperationCapabilities.UpdateRanking;
            }
            if (!isPlaylistMissing)
            {
                capabilities |= ChartOperationCapabilities.RunBmsEncodingCheck
                    | ChartOperationCapabilities.RunBmsEncodingFix
                    | ChartOperationCapabilities.RunZeroNoteCheck
                    | ChartOperationCapabilities.RenameInvalidExtension
                    | ChartOperationCapabilities.ConvertToAudio;
            }
            if (hasPath && !isPlaylistMissing && sourceScope != ChartOperationSourceScope.PendingPackage)
            {
                capabilities |= ChartOperationCapabilities.RepairInstalledLocation;
            }
        }
        if (hasPath && !isPlaylistMissing)
        {
            if (sourceScope != ChartOperationSourceScope.PendingPackage)
            {
                capabilities |= ChartOperationCapabilities.MoveInLibrary | ChartOperationCapabilities.RemoveFromLibrary;
            }
            else if (!isPlaylistRow)
            {
                capabilities |= ChartOperationCapabilities.UpdateInstallDestination;
            }
        }
        return capabilities;
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
    }

    private static bool IsValidSha256(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && Sha256HashRegex.IsMatch(value.Trim());
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return null;
        }
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// 行の表示用タイトルを取得します。
    /// </summary>
    internal static string GetDisplayTitle(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.Title,
            LibraryChartRow libraryChartRow => libraryChartRow.Title,
            BMSFile bmsFile => bmsFile.Title,
            _ => string.Empty
        };
    }

    /// <summary>
    /// 行の表示用サブタイトルを取得します。
    /// </summary>
    internal static string GetDisplaySubtitle(object row)
    {
        if (row is PlaylistDetailRow)
        {
            return string.Empty;
        }
        if (row is LibraryChartRow libraryChartRow)
        {
            return libraryChartRow.BmsFile?.subtitle ?? string.Empty;
        }
        if (row is BMSFile bmsFile)
        {
            return bmsFile.subtitle ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// 行の表示用アーティストを取得します。
    /// </summary>
    internal static string GetDisplayArtist(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.Artist,
            LibraryChartRow libraryChartRow => libraryChartRow.Artist,
            BMSFile bmsFile => bmsFile.Artist,
            _ => string.Empty
        };
    }

    /// <summary>
    /// 行が playlist セル編集を許可するかを返します。
    /// </summary>
    internal static bool CanEditPlaylistCell(object row, string propertyName)
    {
        BMSTableEntry entry = GetPlaylistEntry(row);
        if (entry?.parent == null)
        {
            return false;
        }
        if (!entry.parent.is_external_sync)
        {
            return true;
        }
        return string.Equals(propertyName, nameof(PlaylistDetailRow.memo), StringComparison.Ordinal);
    }
}
