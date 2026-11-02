using System;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class PlaylistSummaryRow : INotifyPropertyChanged
{
    /// <summary>確定した編集属性と派生表示の変更を現在行のBindingへ通知します。</summary>
    [field: NonSerialized]
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>保存headerと公開済み設定だけから対象行を投影します。件数・STATUS・表参照は変更しません。</summary>
    internal void ApplySavedProperties(PlaylistHeaderValue header, CustomFolderOutputSettingsSnapshot settings, bool notify = true)
    {
        PlaylistId = header.PlaylistId;
        Name = header.Name ?? string.Empty;
        FolderName = BMSTable.ResolveOutputDirectoryName(header.Name, header.OutputDirectory) ?? string.Empty;
        string explicitDirectory = BMSTable.NormalizeOutputDirectoryName(header.OutputDirectory);
        FolderNameUndefined = string.IsNullOrWhiteSpace(explicitDirectory)
            || string.Equals(explicitDirectory, BMSTable.CreateDefaultOutputDirectoryName(header.Name), StringComparison.Ordinal);
        CompatPrefix = header.CompatPrefix ?? string.Empty;
        Symbol = header.Symbol ?? string.Empty;
        LastUpdate = header.LastUpdate;
        HeaderUri = BMSTable.ResolvePlaylistUri(header.HeaderUrl, header.PageUrl);
        DataUri = BMSTable.ResolvePlaylistUri(header.DataUrl, HeaderUri ?? header.PageUrl);
        Uri page = header.PageUrl ?? HeaderUri;
        LinkUri = page?.IsAbsoluteUri == true && page.Scheme != "bmseeker" ? page : null;
        IsExternalSync = header.IsExternalSync;
        IsRootFolder = header.IsRootFolder;
        BmtSort = header.BmtSort ?? int.MaxValue;
        IsBmtOutput = header.IsBmtOutput != false;
        OutputBaseName = header.OutputBaseName ?? string.Empty;
        OutputBaseDisplayName = CustomFolderOutputBaseRegistry.GetDisplayName(header.OutputBaseName,
            settings.LR2CustomFolderOutputBaseDir, settings.LR2CustomFolderAdditionalOutputBaseDirs);
        if (notify) { PublishSavedProperties(); }
    }

    /// <summary>初回構築・再表示で現在の生存表headerを同じ投影へ渡します。</summary>
    internal void ApplySavedProperties(BMSTable table, CustomFolderOutputSettingsSnapshot settings, bool notify = true)
    {
        PlaylistHeaderValue header;
        using (table.ReaderWriterLock.GetReaderGuard()) { header = PlaylistHeaderValue.Capture(table); }
        ApplySavedProperties(header, settings, notify);
    }

    /// <summary>現在行の全header属性とURI派生属性をBindingへ通知します。</summary>
    internal void PublishSavedProperties()
    {
        foreach (string property in new[] { nameof(Name), nameof(FolderName), nameof(FolderNameUndefined), nameof(CompatPrefix),
            nameof(Symbol), nameof(LastUpdate), nameof(LinkUri), nameof(HeaderUri), nameof(DataUri), nameof(HeaderUriText),
            nameof(DataUriText), nameof(IsExternalSync), nameof(IsRootFolder), nameof(BmtSort), nameof(IsBmtOutput),
            nameof(OutputBaseName), nameof(OutputBaseDisplayName) })
        { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property)); }
    }
    public int? PlaylistId { get; set; }

    public string OutputBaseName { get; set; } = string.Empty;

    public string OutputBaseDisplayName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string FolderName { get; set; } = string.Empty;

    public bool FolderNameUndefined { get; set; }

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

    /// <summary>ブラウザーで開ける表ページのURIです。内蔵表などページがない場合は null です。</summary>
    public Uri LinkUri { get; set; }

    public Uri HeaderUri { get; set; }

    public Uri DataUri { get; set; }

    public string HeaderUriText => HeaderUri?.ToString() ?? string.Empty;

    public string DataUriText => DataUri?.ToString() ?? string.Empty;

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
