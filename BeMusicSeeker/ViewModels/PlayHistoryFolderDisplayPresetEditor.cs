using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 設定ダイアログで編集するプレイログ FOLDER 表示プリセットです。
/// 永続化形式は <see cref="PlayHistoryDisplayTargetSet"/> に揃え、設定画面だけが編集用の可変状態を持ちます。
/// </summary>
public sealed class PlayHistoryFolderDisplayPresetEditor : ViewModel
{
    private string name;

    /// <summary>
    /// 編集用プリセットを初期化します。
    /// </summary>
    /// <param name="name">ユーザー表示名。</param>
    /// <param name="targets">プリセットに含める playlist 参照。</param>
    internal PlayHistoryFolderDisplayPresetEditor(string name, IEnumerable<PlayHistoryDisplayTargetReference> targets)
    {
        this.name = name ?? string.Empty;
        Targets = [.. targets ?? []];
    }

    /// <summary>
    /// プリセット名を取得または設定します。
    /// </summary>
    public string Name
    {
        get => name;
        set
        {
            string nextName = value ?? string.Empty;
            if (!string.Equals(name, nextName, StringComparison.Ordinal))
            {
                name = nextName;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// プリセットに含める playlist 参照を取得します。
    /// </summary>
    internal ObservableCollection<PlayHistoryDisplayTargetReference> Targets { get; }

    /// <summary>
    /// 保存形式へ変換します。
    /// </summary>
    /// <returns>現在の編集内容を表す target set。</returns>
    internal PlayHistoryDisplayTargetSet ToTargetSet()
    {
        return new PlayHistoryDisplayTargetSet
        {
            Name = Name,
            Targets = [.. Targets.Select(CloneReference)]
        };
    }

    private static PlayHistoryDisplayTargetReference CloneReference(PlayHistoryDisplayTargetReference reference)
    {
        return new PlayHistoryDisplayTargetReference
        {
            PlaylistId = reference?.PlaylistId
        };
    }
}

/// <summary>
/// play history FOLDER 表示プリセットの別ウィンドウ編集に使う一時セッションです。
/// 設定ダイアログ本体の draft を Cancel で汚さないため、名前と playlist 選択状態をコピーして保持します。
/// </summary>
public sealed class PlayHistoryFolderDisplayPresetEditSession : ViewModel
{
    private string name;

    /// <summary>
    /// 編集セッションを初期化します。
    /// </summary>
    /// <param name="sourcePreset">編集元プリセット。新規追加時は <c>null</c>。</param>
    /// <param name="name">初期プリセット名。</param>
    /// <param name="playlistOptions">対象 playlist 候補。</param>
    internal PlayHistoryFolderDisplayPresetEditSession(
        PlayHistoryFolderDisplayPresetEditor sourcePreset,
        string name,
        IEnumerable<PlayHistoryFolderPresetPlaylistOption> playlistOptions)
    {
        SourcePreset = sourcePreset;
        this.name = name ?? string.Empty;
        PlaylistOptions = [.. playlistOptions ?? []];
    }

    /// <summary>
    /// 編集元プリセットを取得します。
    /// 新規追加セッションでは <c>null</c> になり、OK 時に一覧へ追加します。
    /// </summary>
    internal PlayHistoryFolderDisplayPresetEditor SourcePreset { get; }

    /// <summary>
    /// 編集中のプリセット名を取得または設定します。
    /// </summary>
    public string Name
    {
        get => name;
        set
        {
            string nextName = value ?? string.Empty;
            if (!string.Equals(name, nextName, StringComparison.Ordinal))
            {
                name = nextName;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// 対象 playlist 候補を取得します。
    /// </summary>
    public ObservableCollection<PlayHistoryFolderPresetPlaylistOption> PlaylistOptions { get; }

    /// <summary>
    /// 編集内容を保存形式へ変換します。
    /// </summary>
    /// <returns>現在の名前と選択 playlist を持つ target set。</returns>
    internal PlayHistoryDisplayTargetSet ToTargetSet()
    {
        return new PlayHistoryDisplayTargetSet
        {
            Name = Name,
            Targets =
            [
                .. PlaylistOptions
                    .Where(option => option.IsSelected)
                    .Select(option => option.ToReference())
            ]
        };
    }
}

/// <summary>
/// プレイログ FOLDER 表示プリセットに追加できる playlist 候補です。
/// </summary>
public sealed class PlayHistoryFolderPresetPlaylistOption : ViewModel
{
    private readonly Action<PlayHistoryFolderPresetPlaylistOption> selectionChanged;

    private bool isSelected;

    /// <summary>
    /// playlist 候補を初期化します。
    /// </summary>
    /// <param name="table">候補 playlist。</param>
    /// <param name="isSelected">現在のプリセットに含まれているか。</param>
    /// <param name="selectionChanged">選択状態変更時の通知先。</param>
    internal PlayHistoryFolderPresetPlaylistOption(
        PlaylistTablePresentationSnapshot table,
        bool isSelected,
        Action<PlayHistoryFolderPresetPlaylistOption> selectionChanged)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        this.isSelected = isSelected;
        this.selectionChanged = selectionChanged;
        DisplayName = BuildDisplayName(table);
    }

    /// <summary>
    /// 候補 playlist を取得します。
    /// </summary>
    internal PlaylistTablePresentationSnapshot Table { get; }

    /// <summary>
    /// 設定ダイアログに表示する playlist 名を取得します。
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// 現在のプリセットに含めるかどうかを取得または設定します。
    /// </summary>
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected != value)
            {
                isSelected = value;
                RaisePropertyChanged();
                selectionChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// 保存形式へ変換します。
    /// </summary>
    /// <returns>playlist.id を主キーとする target reference。</returns>
    internal PlayHistoryDisplayTargetReference ToReference()
    {
        return new PlayHistoryDisplayTargetReference
        {
            PlaylistId = Table.PlaylistId
        };
    }

    /// <summary>
    /// 指定された playlist 参照がこの候補を指しているかどうかを判定します。
    /// </summary>
    /// <param name="reference">保存済み playlist 参照。</param>
    /// <returns>参照がこの候補を指している場合は <c>true</c>。</returns>
    internal bool Matches(PlayHistoryDisplayTargetReference reference)
    {
        return Matches(Table, reference);
    }

    /// <summary>
    /// 指定された playlist と保存済み参照が一致するかどうかを判定します。
    /// </summary>
    internal static bool Matches(PlaylistTablePresentationSnapshot table, PlayHistoryDisplayTargetReference reference)
    {
        return reference?.PlaylistId.HasValue == true
            && table?.PlaylistId == reference.PlaylistId;
    }

    private static string BuildDisplayName(PlaylistTablePresentationSnapshot table)
    {
        string name = NormalizeText(table.Name) ?? NormalizeText(table.OriginalName) ?? "(playlist)";
        string symbol = NormalizeText(table.Symbol) ?? NormalizeText(table.OriginalSymbol);
        string id = table.PlaylistId?.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(symbol)
            ? name + FormatId(id)
            : name + " [" + symbol + "]" + FormatId(id);
    }

    private static string FormatId(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? string.Empty : " #" + id;
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
