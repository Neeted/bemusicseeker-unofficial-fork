using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class PlaylistSummaryRow
{
    public int? PlaylistId { get; set; }

    public string OutputBaseName { get; set; } = string.Empty;

    public string OutputBaseDisplayName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// LR2 互換フォルダ名へ付与する接頭辞です。
    /// </summary>
    public string CompatPrefix { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public DateTime LastUpdate { get; set; }

    public int TotalCharts { get; set; }

    public int OwnedCharts { get; set; }

    public int MissingCharts { get; set; }

    public double OwnedRatio { get; set; }

    public Uri LinkUri { get; set; }

    public bool IsExternalSync { get; set; }

    public string Status { get; set; } = string.Empty;

    public string StatusDetail { get; set; } = string.Empty;

    public int StatusSortOrder { get; set; }

    public bool HasFailureStatus { get; set; }

    public bool IsRootFolder { get; set; }

    public int BmtSort { get; set; }

    public bool IsBmtOutput { get; set; }

    public BMSTable TableRef { get; set; }
}
