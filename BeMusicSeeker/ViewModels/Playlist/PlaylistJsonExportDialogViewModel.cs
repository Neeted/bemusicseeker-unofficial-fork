#nullable enable
using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Livet;
using Resources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.ViewModels;

/// <summary>一回のJSON出力で選択した二つの保存先です。正本のURLとは独立して扱います。</summary>
internal sealed record PlaylistJsonExportPaths(string HeaderPath, string DataPath);

/// <summary>JSON保存先の入力と、画面・書込み入口で共有する拒否条件を保持します。</summary>
internal sealed class PlaylistJsonExportDialogViewModel : ViewModel
{
    private string headerPath = "";
    private string dataPath = "";

    /// <summary>ヘッダーの保存先です。</summary>
    public string HeaderPath
    {
        get => headerPath;
        set { headerPath = value; RaisePropertyChanged(); RefreshValidation(); }
    }

    /// <summary>データの保存先です。</summary>
    public string DataPath
    {
        get => dataPath;
        set { dataPath = value; RaisePropertyChanged(); RefreshValidation(); }
    }

    /// <summary>未入力・同一保存先・正規化不能の理由です。有効時は空文字列です。</summary>
    public string ValidationMessage => ValidatePaths(HeaderPath, DataPath);

    /// <summary>両方の保存先が別のファイルを指定しているかを返します。</summary>
    public bool CanExport => ValidationMessage.Length == 0;

    /// <summary>画面の確定結果を変更不能な入力へ捕捉します。保存入口でも再検査します。</summary>
    internal PlaylistJsonExportPaths CapturePaths() => new(HeaderPath, DataPath);

    /// <summary>ファイルの存在に依存せず、空欄と正規化後の同一パスを拒否します。リンク先の同一性は判定しません。</summary>
    internal static string ValidatePaths(string? header, string? data)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(data))
        {
            return Resources.Playlist_json_export_paths_required;
        }
        try
        {
            return string.Equals(LongPathFileSystem.NormalizePathForStorage(header),
                LongPathFileSystem.NormalizePathForStorage(data), StringComparison.OrdinalIgnoreCase)
                ? Resources.Playlist_json_export_paths_same : "";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Resources.Playlist_json_export_path_invalid;
        }
    }

    private void RefreshValidation()
    {
        RaisePropertyChanged(nameof(ValidationMessage));
        RaisePropertyChanged(nameof(CanExport));
    }
}
