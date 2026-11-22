using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 統合候補一覧の列の幅・表示位置・表示状態です。起動中だけ保持し、設定へ保存しません。
/// 画面の表が購読するため、各列は変更を同期で通知する <see cref="PlaylistSummaryColumnSettings.ColumnLayout"/> を使います。
/// </summary>
public sealed class DuplicateMergeCandidateColumnSettings
{
    /// <summary>SELECT列（グループ結合のチェックボックス）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Select { get; } = new() { Width = 64 };

    /// <summary>STATUS列（グループ結合の文字）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Status { get; } = new() { Width = 160 };

    /// <summary>KEEP列（ラジオボタン）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Keep { get; } = new() { Width = 56 };

    /// <summary>PATH列。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Path { get; } = new() { Width = 480 };

    /// <summary>FOLDER列（フォルダ名）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Folder { get; } = new() { Width = 200 };

    /// <summary>TITLE列。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Title { get; } = new() { Width = 240 };

    /// <summary>ARTIST列。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Artist { get; } = new() { Width = 200 };
}
