using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// プレイリストツリーで使用する特殊ノード種別を表します。
/// </summary>
public enum PlaylistFolderNodeSpecialKind
{
    /// <summary>
    /// 通常フォルダを表します。
    /// </summary>
    None = 0,

    /// <summary>
    /// 所持していない譜面をまとめる特殊ノードを表します。
    /// </summary>
    NotOwned
}

/// <summary>
/// プレイリストツリーの子ノードを表します。
/// 通常フォルダと特殊ノードの両方を同じ型で扱うための表示モデルです。
/// </summary>
public sealed class PlaylistFolderNode
{
    /// <summary>
    /// 通常フォルダの論理名を取得します。
    /// 特殊ノードでは空文字列です。
    /// </summary>
    public string FolderName { get; }

    /// <summary>
    /// このノードの特殊種別を取得します。
    /// 通常フォルダの場合は <see cref="PlaylistFolderNodeSpecialKind.None"/> です。
    /// </summary>
    public PlaylistFolderNodeSpecialKind SpecialKind { get; }

    /// <summary>
    /// このノードが特殊ノードかどうかを取得します。
    /// </summary>
    public bool IsSpecial => SpecialKind != PlaylistFolderNodeSpecialKind.None;

    /// <summary>
    /// このノードが編集可能かどうかを取得します。
    /// 特殊ノードは編集不可です。
    /// </summary>
    public bool IsEditable => !IsSpecial;

    /// <summary>
    /// このノードがドロップ先として利用可能かどうかを取得します。
    /// 特殊ノードはドロップ不可です。
    /// </summary>
    public bool IsDroppable => !IsSpecial;

    /// <summary>
    /// ツリー上に表示する文字列を取得します。
    /// 通常フォルダではフォルダ名を返し、空フォルダ名は <c>(フォルダ名なし)</c> を返します。
    /// 特殊ノードでは種別に対応する表示名を返します。
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (IsSpecial)
            {
                return SpecialKind.ToDisplayName();
            }
            if (!string.IsNullOrWhiteSpace(FolderName))
            {
                return FolderName;
            }
            return "(" + Resources.No_folder_name + ")";
        }
    }

    private PlaylistFolderNode(string folderName, PlaylistFolderNodeSpecialKind specialKind)
    {
        FolderName = folderName ?? string.Empty;
        SpecialKind = specialKind;
    }

    /// <summary>
    /// 通常フォルダノードを生成します。
    /// </summary>
    /// <param name="folderName">論理フォルダ名。空文字列の場合は「フォルダ名なし」表示になります。</param>
    /// <returns>通常フォルダを表すノード。</returns>
    public static PlaylistFolderNode CreateFolder(string folderName)
    {
        return new PlaylistFolderNode(folderName, PlaylistFolderNodeSpecialKind.None);
    }

    /// <summary>
    /// 特殊ノードを生成します。
    /// </summary>
    /// <param name="specialKind">生成する特殊ノード種別。</param>
    /// <returns>特殊ノードを表すノード。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="specialKind"/> に <see cref="PlaylistFolderNodeSpecialKind.None"/> が指定された場合。
    /// </exception>
    public static PlaylistFolderNode CreateSpecial(PlaylistFolderNodeSpecialKind specialKind)
    {
        if (specialKind == PlaylistFolderNodeSpecialKind.None)
        {
            throw new ArgumentOutOfRangeException("specialKind");
        }
        return new PlaylistFolderNode(string.Empty, specialKind);
    }
}

/// <summary>
/// <see cref="PlaylistFolderNodeSpecialKind"/> の表示文字列変換を提供します。
/// </summary>
internal static class PlaylistFolderNodeSpecialKindExt
{
    /// <summary>
    /// 特殊ノード種別に対応する表示名を取得します。
    /// </summary>
    /// <param name="specialKind">変換対象の特殊ノード種別。</param>
    /// <returns>ツリー表示用の文字列。</returns>
    internal static string ToDisplayName(this PlaylistFolderNodeSpecialKind specialKind)
    {
        return specialKind switch
        {
            PlaylistFolderNodeSpecialKind.NotOwned => "[NO SONG]",
            _ => string.Empty
        };
    }
}
