using System;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// main 一覧の行オブジェクトを、通常譜面行と playlist lightweight row の両方に対して解決します。
/// </summary>
internal static class GridRowResolver
{
    private static readonly Regex Md5HashRegex = new("^[a-f0-9]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sha256HashRegex = new("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// playlist 詳細表示用 row かどうかを返します。
    /// </summary>
    internal static bool IsPlaylistRow(object row)
    {
        return row is PlaylistDetailRow or PlaylistDetailSourceRow;
    }

    /// <summary>
    /// 行から playlist エントリを取得します。
    /// </summary>
    internal static BMSTableEntry GetPlaylistEntry(object row)
    {
        switch (row)
        {
            case PlaylistDetailRow playlistDetailRow:
                return playlistDetailRow.Entry;
            case PlaylistDetailSourceRow playlistSourceRow:
                return playlistSourceRow.Entry;
            default:
                return null;
        }
    }

    /// <summary>
    /// 行から BMS player 用の BMS storage row を取得します。
    /// bmson は現時点では再生対象にせず、この BMS-only 境界では返しません。
    /// </summary>
    internal static bool TryGetBmsPlayerFile(object row, out BMSFile file)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            file = playlistDetailRow.BmsStorageOwner ?? playlistDetailRow.Chart?.GetBmsStorageOwner();
        }
        else if (row is PlaylistDetailSourceRow playlistSourceRow)
        {
            file = playlistSourceRow.BmsPlayerFile ?? playlistSourceRow.Chart?.GetBmsStorageOwner();
        }
        else if (row is LibraryChartRow libraryChartRow)
        {
            file = libraryChartRow.GetBmsStorageOwner();
        }
        else
        {
            file = row as BMSFile;
        }
        return file != null;
    }

    internal static bool TryGetChartFile(object row, out ChartFile chart)
    {
        return TryGetChartFileCore(row, out chart);
    }

    private static bool TryGetChartFileCore(object row, out ChartFile chart)
    {
        chart = null;
        switch (row)
        {
            case PlaylistDetailRow playlistDetailRow:
                chart = playlistDetailRow.Chart;
                return chart != null;
            case PlaylistDetailSourceRow playlistSourceRow:
                chart = playlistSourceRow.Chart;
                return chart != null;
            case LibraryChartRow libraryChartRow:
                chart = libraryChartRow.Chart;
                return chart != null;
            default:
                return false;
        }
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
        if (!TryGetChartFileCore(row, out ChartFile chart))
        {
            return false;
        }
        BMSTableEntry playlistEntry = GetPlaylistEntry(row);
        bool isPlaylistRow = IsPlaylistRow(row);
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
        ChartFile operationChart = ResolveOperationChart(row, chart);
        ChartOperationCapabilities capabilities = BuildCapabilities(operationChart, playlistEntry, sourceScope, isPlaylistRow, isOwned, isPlaylistMissing);
        target = new ChartOperationTarget(operationChart, playlistEntry, sourceScope, isOwned, isPending, isPlaylistMissing, capabilities, ResolvePackageEntry(row));
        return true;
    }

    internal static bool TryGetFolderEditChartOperationTarget(object row, ChartOperationSourceScope sourceScope, out ChartOperationTarget target)
    {
        if (!TryGetChartOperationTarget(row, sourceScope, out target)
            || !target.HasCapability(ChartOperationCapabilities.MoveInLibrary)
            || string.IsNullOrWhiteSpace(target.Chart?.Path))
        {
            target = null;
            return false;
        }

        return true;
    }

    internal static bool IsBmsonChartRow(object row)
    {
        return TryGetChartFile(row, out ChartFile chart) && chart.Kind == ChartFileKind.Bmson;
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
        if (row is PlayHistoryRow playHistoryRow)
        {
            if (playHistoryRow.ResolvedChart == null)
            {
                return null;
            }
            return FirstNonEmpty(playHistoryRow.ResolvedChart?.Md5, playHistoryRow.RawHash);
        }
        if (TryGetChartFile(row, out ChartFile chart))
        {
            return chart.Md5;
        }
        return null;
    }

    /// <summary>
    /// 外部の本体パッケージ検索に使う playlist 行の MD5 を取得します。
    /// playlist 定義上の MD5 を優先し、所持済み行で entry 側が空の場合だけ解決済み譜面の MD5 にフォールバックします。
    /// </summary>
    internal static string GetPlaylistExternalPackageLookupMd5(object row)
    {
        if (!IsPlaylistRow(row))
        {
            return null;
        }
        string entryMd5 = GetPlaylistEntry(row)?.md5;
        if (IsMd5Hash(entryMd5))
        {
            return entryMd5.ToLowerInvariant();
        }
        string resolvedMd5 = GetHash(row);
        return IsMd5Hash(resolvedMd5) ? resolvedMd5.ToLowerInvariant() : null;
    }

    private static bool IsMd5Hash(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, StringComparison.OrdinalIgnoreCase)
            && Md5HashRegex.IsMatch(value);
    }

    /// <summary>
    /// 行の SHA256 ハッシュを取得します。
    /// </summary>
    internal static string GetSha256(object row)
    {
        if (row is PlayHistoryRow playHistoryRow)
        {
            if (playHistoryRow.ResolvedChart == null)
            {
                return null;
            }
            return FirstNonEmpty(playHistoryRow.Sha256, playHistoryRow.ResolvedChart?.Sha256, playHistoryRow.ResolvedChart?.ChartInfo?.sha256);
        }
        if (TryGetChartFile(row, out ChartFile chart))
        {
            return chart.Sha256;
        }
        return null;
    }

    /// <summary>
    /// Mocha / MinIR など SHA256 ベースの外部 repository 連携に使うハッシュを取得します。
    /// 表示用 row に materialize 済みの値を優先し、通常 BMS 行では chart_info の SHA256 も fallback にします。
    /// </summary>
    internal static string GetRepositorySha256(object row)
    {
        string sha256 = row is PlayHistoryRow playHistoryRow
            ? playHistoryRow.ResolvedChart == null
                ? null
                : FirstNonEmpty(playHistoryRow.Sha256, playHistoryRow.ResolvedChart?.Sha256, playHistoryRow.ResolvedChart?.ChartInfo?.sha256)
            : TryGetChartFile(row, out ChartFile chart)
                ? FirstNonEmpty(chart.Sha256, chart.ChartInfo?.sha256)
                : null;
        return IsValidSha256(sha256) ? sha256.ToLowerInvariant() : null;
    }

    private static PackageChartEntry ResolvePackageEntry(object row)
    {
        return row is LibraryChartRow libraryChartRow ? libraryChartRow.PackageEntry : null;
    }

    private static ChartFile ResolveOperationChart(object row, ChartFile chart)
    {
        if (chart?.Kind == ChartFileKind.Bms
            && chart.GetBmsStorageOwner() == null
            && row is PlaylistDetailRow playlistDetailRow
            && playlistDetailRow.BmsStorageOwner != null)
        {
            return ChartFileProjection.FromBmsStorageOwnerIdentity(playlistDetailRow.BmsStorageOwner) ?? chart;
        }
        if (chart?.Kind == ChartFileKind.Bms
            && chart.GetBmsStorageOwner() == null
            && row is PlaylistDetailSourceRow playlistSourceRow
            && playlistSourceRow.BmsPlayerFile != null)
        {
            return ChartFileProjection.FromBmsStorageOwnerIdentity(playlistSourceRow.BmsPlayerFile) ?? chart;
        }
        return chart;
    }

    private static ChartOperationCapabilities BuildCapabilities(
        ChartFile chart,
        BMSTableEntry playlistEntry,
        ChartOperationSourceScope sourceScope,
        bool isPlaylistRow,
        bool isOwned,
        bool isPlaylistMissing)
    {
        ChartOperationCapabilities capabilities = ChartOperationCapabilities.None;
        bool hasPath = !string.IsNullOrWhiteSpace(chart.Path);
        bool hasMd5 = !string.IsNullOrWhiteSpace(chart.Md5);
        bool isBms = chart.Kind == ChartFileKind.Bms;
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
            if (IsValidMd5(chart.Md5))
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
        }
        if (hasPath && !isPlaylistMissing && sourceScope != ChartOperationSourceScope.PendingPackage)
        {
            capabilities |= ChartOperationCapabilities.RepairInstalledLocation;
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

    private static bool IsValidSha256(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && Sha256HashRegex.IsMatch(value.Trim());
    }

    private static bool IsValidMd5(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && Md5HashRegex.IsMatch(value.Trim());
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
        if (TryGetChartFile(row, out ChartFile chart))
        {
            return chart.Title;
        }
        return string.Empty;
    }

    /// <summary>
    /// 行の表示用タイトルから subtitle を含まない title 部分を取得します。
    /// </summary>
    internal static string GetDisplayRawTitle(object row)
    {
        if (TryGetChartFile(row, out ChartFile chart))
        {
            return string.IsNullOrWhiteSpace(chart.RawTitle) ? chart.Title : chart.RawTitle;
        }
        return string.Empty;
    }

    /// <summary>
    /// 行の表示用サブタイトルを取得します。
    /// </summary>
    internal static string GetDisplaySubtitle(object row)
    {
        if (TryGetChartFile(row, out ChartFile chart))
        {
            return chart.Subtitle ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// 行の表示用アーティストを取得します。
    /// </summary>
    internal static string GetDisplayArtist(object row)
    {
        if (TryGetChartFile(row, out ChartFile chart))
        {
            return chart.Artist;
        }
        return string.Empty;
    }

    /// <summary>
    /// BMS player controls に表示する BMS storage row のタイトルを取得します。
    /// </summary>
    internal static string GetBmsPlayerDisplayTitle(BMSFile file)
    {
        return file?.GetRawTitleForDisplay() ?? string.Empty;
    }

    /// <summary>
    /// BMS player controls に表示する BMS storage row のサブタイトルを取得します。
    /// </summary>
    internal static string GetBmsPlayerDisplaySubtitle(BMSFile file)
    {
        return file?.subtitle ?? string.Empty;
    }

    /// <summary>
    /// BMS player controls に表示する BMS storage row のアーティストを取得します。
    /// </summary>
    internal static string GetBmsPlayerDisplayArtist(BMSFile file)
    {
        return file?.Artist ?? string.Empty;
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
