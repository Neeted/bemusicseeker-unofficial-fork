using System;
using System.ComponentModel;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class PlaylistSummaryRow : INotifyPropertyChanged
{
    /// <summary>確定した編集属性と派生表示の変更を現在行のBindingへ通知します。</summary>
    [field: NonSerialized]
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>保存済み正本の編集属性と派生表示を対象行だけへ反映します。rawcacheは無通知で進め、現在行だけBindingへ通知します。</summary>
    internal void ApplySavedProperties(BMSTable table, bool notify = true)
    {
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            Name = table.name ?? string.Empty;
            FolderName = table.Output_dir ?? string.Empty;
            string explicitDirectory = BMSTable.NormalizeOutputDirectoryName(table.output_dir);
            FolderNameUndefined = string.IsNullOrWhiteSpace(explicitDirectory)
                || string.Equals(explicitDirectory, BMSTable.CreateDefaultOutputDirectoryName(table.name), StringComparison.Ordinal);
            CompatPrefix = table.compat_prefix ?? string.Empty;
            Symbol = table.symbol ?? string.Empty;
            LastUpdate = table.last_update;
        }
        if (notify) { PublishSavedProperties(); }
    }

    /// <summary>現在表示への確定属性通知を、collectionの局所変更と同じUI処理で発行します。</summary>
    internal void PublishSavedProperties()
    {
        foreach (string property in new[] { nameof(Name), nameof(FolderName), nameof(FolderNameUndefined), nameof(CompatPrefix), nameof(Symbol), nameof(LastUpdate) })
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
