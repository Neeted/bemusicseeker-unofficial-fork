using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複譜面の一括整理と重複音源の整理が、削除するファイルをどう削除するかです。統合候補一覧・整理候補一覧の「削除」の値であり、
/// 整理の要求が操作全体で一つ持ちます（グループ・フォルダ・削除の種類ごとには変わりません）。
/// 画面の束縛（XAML）から参照するため public です。
/// </summary>
public enum DuplicateOrganizeDeleteMethod
{
    /// <summary>ごみ箱へ移動します（既定）。</summary>
    MoveToRecycleBin,

    /// <summary>ごみ箱を経由せず、完全に削除します。削除したファイルは元に戻せません。</summary>
    DeletePermanently
}

/// <summary><see cref="DuplicateOrganizeDeleteMethod"/> を、削除の窓口の指定へ変換します。</summary>
internal static class DuplicateOrganizeDeleteMethodExtensions
{
    /// <summary>削除の窓口（<see cref="IFileMutationService.DeleteFileShell"/>）の <see cref="RecycleOption"/> へ変換します。</summary>
    internal static RecycleOption ToRecycleOption(this DuplicateOrganizeDeleteMethod method)
        => method == DuplicateOrganizeDeleteMethod.DeletePermanently ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
}
