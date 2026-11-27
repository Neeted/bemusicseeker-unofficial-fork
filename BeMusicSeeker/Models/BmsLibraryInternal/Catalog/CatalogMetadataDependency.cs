using System;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 所持譜面の原題・作者・所属に依存する派生情報（ディレクトリ別のメタデータプロフィールと本体候補の索引）が、
/// 同じ変更で失効・更新される条件と、親フォルダのキーの作り方を一か所に定義します。
/// </summary>
internal static class CatalogMetadataDependency
{
    /// <summary>
    /// 追加・削除、パス・原題・作者（BMSは元の作者、bmsonは現在の作者）の変更のいずれかなら true です。
    /// スコア・健康度・文字コード・ハッシュだけの変更や、BMSの表示用作者だけの変更では false です。
    /// </summary>
    internal static bool Affects(CatalogChartChange change)
        => change.Before == null || change.After == null
            || change.Before.Path != change.After.Path || change.Before.RawTitle != change.After.RawTitle
            || (change.Before.Kind == ChartFileKind.Bms ? change.Before.RawArtist != change.After.RawArtist : change.Before.Artist != change.After.Artist);

    /// <summary>
    /// 保存パスを物理パスへ正規化し、その直接の親ディレクトリを返します。正規化できない保存値では false です（既存の受理範囲を狭めません）。
    /// 末尾の区切りは取り除きません。
    /// </summary>
    internal static bool TryGetContainingDirectory(string path, out string directory)
    {
        directory = null;
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try { directory = Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(directory);
    }

    /// <summary>ディレクトリ別のキーとして使う表記（末尾の区切りを除いたもの）です。</summary>
    internal static string ToFolderKey(string directory) => LongPathFileSystem.TrimTrailingDirectorySeparators(directory);

    /// <summary>保存パスの直接の親フォルダのキーを返します。作れない場合は null です。</summary>
    internal static string TryBuildParentFolderKey(string path)
        => TryGetContainingDirectory(path, out string directory) ? ToFolderKey(directory) : null;
}
