using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// main DataGrid の行オブジェクトを、通常譜面行と playlist lightweight row の両方に対して解決します。
/// </summary>
internal static class GridRowResolver
{
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
    /// 行から実体譜面を取得します。
    /// </summary>
    internal static BMSFile GetRealBmsFile(object row)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.RealFile;
        }
        return row as BMSFile;
    }

    /// <summary>
    /// 行から操作対象の譜面を取得します。
    /// playlist 行で実体譜面が無い場合は null を返します。
    /// </summary>
    internal static BMSFile GetOperationBmsFile(object row)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.RealFile;
        }
        return row as BMSFile;
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
            BMSFile bmsFile => bmsFile.hash,
            _ => null
        };
    }

    /// <summary>
    /// 行の表示用タイトルを取得します。
    /// </summary>
    internal static string GetDisplayTitle(object row)
    {
        return row switch
        {
            PlaylistDetailRow playlistDetailRow => playlistDetailRow.Title,
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
